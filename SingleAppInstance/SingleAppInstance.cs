// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SingleAppInstance;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// Provides a mechanism to ensure that only one instance of an application is running at a time.
/// </summary>
public static class SingleAppInstance
{
	// Built from plain BCL strings, not ktsu.AppDataStorage / ktsu.Semantics.Paths: those are not trim-
	// or AOT-safe, and these initializers run on the first ShouldLaunch(), so a trimmed or Native AOT
	// app would crash before it could decide anything (#221). The folder is the one AppData.Path
	// resolves to, ApplicationData / AppDomain friendly name, so existing installs keep their PID file.
	internal static string PidDirectoryPath { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
		AppDomain.CurrentDomain.FriendlyName);
	internal static string PidFilePath { get; } = Path.Combine(PidDirectoryPath, $".{nameof(SingleAppInstance)}.pid");

	/// <summary>
	/// Exits the application if another instance is already running.
	/// </summary>
	/// <remarks>
	/// This method checks if another instance of the application is already running by calling <see cref="ShouldLaunch"/>.
	/// If another instance is detected, the current application exits with a status code of 0.
	/// </remarks>
	/// <exception cref="IOException">The PID file or its folder cannot be read or written.</exception>
	/// <exception cref="UnauthorizedAccessException">Access to the PID file or its folder is denied.</exception>
	public static void ExitIfAlreadyRunning()
	{
		if (!ShouldLaunch())
		{
			Environment.Exit(0);
		}
	}

	/// <summary>
	/// Determines whether the application should launch.
	/// </summary>
	/// <returns>
	/// <c>true</c> if the application should launch; otherwise, <c>false</c>.
	/// </returns>
	/// <remarks>
	/// This method checks if there is already an instance of the application running.
	/// If no other instance is running, it writes the current process ID to a PID file
	/// and waits for a short period to handle potential race conditions. It then checks
	/// again to ensure no other instance started during the wait period.
	/// If the PID file stays locked by another process, another instance is taken to be starting
	/// and this method returns <c>false</c> rather than throwing. Any other failure to read or write
	/// the PID file, such as a file or folder left owned by another user, is a lasting fault rather
	/// than a sign of another instance, so it is thrown instead of being reported as one.
	/// </remarks>
	/// <exception cref="IOException">The PID file or its folder cannot be read or written.</exception>
	/// <exception cref="UnauthorizedAccessException">Access to the PID file or its folder is denied.</exception>
	public static bool ShouldLaunch()
	{
		// if there is already an instance running, exit
		if (IsAlreadyRunning())
		{
			return false;
		}

		// if no other instance is running, write our pid to the pid file and wait to see
		// if another instance was attempting to start at the same time
		if (!TryWritePidFile())
		{
			return false;
		}

		Thread.Sleep(1000);

		// in case there was a race and another instance is starting at the same time we
		// need to check again to see if we won the lock. We just wrote a whole PID file, so
		// content that cannot be read now was written by an instance racing us, and it counts
		// as that instance rather than as no instance at all
		return ReadPidFileState() == PidFileState.NoInstance;
	}

	/// <summary>
	/// What the PID file says about other instances of the application.
	/// </summary>
	internal enum PidFileState
	{
		/// <summary>
		/// No other instance is running.
		/// </summary>
		NoInstance,

		/// <summary>
		/// Another instance is running, or is holding the PID file while it starts.
		/// </summary>
		AnotherInstance,

		/// <summary>
		/// The PID file exists but its contents cannot be understood.
		/// </summary>
		Unreadable,
	}

	/// <summary>
	/// How many times the PID file is read or replaced before contention is taken to be another instance.
	/// </summary>
	private const int PidFileAttempts = 5;

	/// <summary>
	/// How long to wait before each retry, multiplied by the number of attempts made so far.
	/// </summary>
	private static readonly TimeSpan PidFileRetryDelay = TimeSpan.FromMilliseconds(20);

	/// <summary>
	/// Represents process information stored in the PID file.
	/// </summary>
	internal class ProcessInfo
	{
		/// <summary>
		/// Gets or sets the process ID.
		/// </summary>
		public int ProcessId { get; set; }

		/// <summary>
		/// Gets or sets the name of the process.
		/// </summary>
		public string? ProcessName { get; set; }

		/// <summary>
		/// Gets or sets the start time of the process.
		/// </summary>
		public DateTime StartTime { get; set; }

		/// <summary>
		/// Gets or sets the main module filename of the process.
		/// </summary>
		public string? MainModuleFileName { get; set; }

		/// <summary>
		/// Gets or sets, on Linux, the process's start time in clock ticks since boot, read from field 22
		/// of <c>/proc/&lt;pid&gt;/stat</c>. Unlike <see cref="StartTime"/>, it does not move when the
		/// wall clock is stepped.
		/// </summary>
		public long? LinuxStartTicks { get; set; }

		/// <summary>
		/// Gets or sets, on Linux, the boot the process started in, read from
		/// <c>/proc/sys/kernel/random/boot_id</c>. <see cref="LinuxStartTicks"/> only names a process
		/// within one boot.
		/// </summary>
		public string? LinuxBootId { get; set; }
	}

	/// <summary>
	/// Checks if there is already an instance of the application running.
	/// </summary>
	/// <returns>
	/// <c>true</c> if another instance is running; otherwise, <c>false</c>.
	/// </returns>
	/// <remarks>
	/// This method reads the PID file to get the process information of the running instance.
	/// It then checks if the process with that ID is still running and verifies it's the same application.
	/// </remarks>
	internal static bool IsAlreadyRunning() => ReadPidFileState() == PidFileState.AnotherInstance;

	/// <summary>
	/// Reads the PID file and determines whether it describes another running instance.
	/// </summary>
	/// <returns>What the PID file says about other instances of the application.</returns>
	/// <remarks>
	/// Another instance may be writing the PID file at the same moment, which on some platforms
	/// makes the read fail with a sharing violation. The read is retried briefly, and if the file
	/// stays locked it is treated as another instance that is starting. Any other failure that
	/// outlasts the retries is thrown, because it will not clear by itself.
	/// </remarks>
	/// <exception cref="IOException">The PID file cannot be read for a reason other than a lock.</exception>
	/// <exception cref="UnauthorizedAccessException">Access to the PID file is denied.</exception>
	internal static PidFileState ReadPidFileState()
	{
		int currentPid = GetCurrentProcessId();
		Exception? lastFailure = null;

		for (int attempt = 1; attempt <= PidFileAttempts; attempt++)
		{
			try
			{
				string pidFileContents = File.ReadAllText(PidFilePath);
				return CheckPidFileContents(pidFileContents, currentPid);
			}
			catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
			{
				// The PID file or its directory doesn't exist - no instance running
				return PidFileState.NoInstance;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Another instance may be writing or holding the PID file
				lastFailure = ex;
			}

			if (attempt < PidFileAttempts)
			{
				Thread.Sleep(TimeSpan.FromTicks(PidFileRetryDelay.Ticks * attempt));
			}
		}

		// A PID file that stayed locked is another instance holding it while it starts
		if (lastFailure is IOException ioException && IsLockViolation(ioException))
		{
			return PidFileState.AnotherInstance;
		}

		throw PidFileFault("read", lastFailure!);
	}

	/// <summary>
	/// The Win32 error a file open fails with when another process has the file open without sharing it.
	/// </summary>
	private const int ErrorSharingViolation = 0x20;

	/// <summary>
	/// The Win32 error a file operation fails with when another process has locked part of the file.
	/// </summary>
	private const int ErrorLockViolation = 0x21;

	/// <summary>
	/// The <c>EWOULDBLOCK</c> errno on Linux, which .NET reports when another process holds the file's lock.
	/// </summary>
	private const int LinuxWouldBlock = 11;

	/// <summary>
	/// The <c>EWOULDBLOCK</c> errno on macOS and FreeBSD, which .NET reports when another process holds the file's lock.
	/// </summary>
	private const int BsdWouldBlock = 35;

	/// <summary>
	/// Checks whether an I/O failure was caused by another process holding the file.
	/// </summary>
	/// <param name="exception">The I/O failure.</param>
	/// <returns><c>true</c> for a sharing or lock violation; otherwise, <c>false</c>.</returns>
	/// <remarks>
	/// Windows reports these as an HRESULT wrapping a Win32 error. On Unix, .NET locks a file opened
	/// with <see cref="FileShare.None"/> with <c>flock</c>, and reports another process holding that lock
	/// with the bare <c>EWOULDBLOCK</c> errno. On a Unix whose errno is not known here, every I/O failure
	/// is still taken to be another instance, as it was before access failures were told apart.
	/// </remarks>
	private static bool IsLockViolation(IOException exception) => IsLockViolation(exception.HResult, CurrentPlatform);

	/// <summary>
	/// FreeBSD, which <see cref="OSPlatform"/> names only on newer frameworks.
	/// </summary>
	private static readonly OSPlatform FreeBsd = OSPlatform.Create("FREEBSD");

	/// <summary>
	/// The platform this process runs on, or <c>null</c> when it is none whose lock error is known.
	/// </summary>
	private static readonly OSPlatform? CurrentPlatform =
		new[] { OSPlatform.Windows, OSPlatform.Linux, OSPlatform.OSX, FreeBsd }
			.Select(platform => (OSPlatform?)platform)
			.FirstOrDefault(platform => RuntimeInformation.IsOSPlatform(platform!.Value));

	/// <summary>
	/// Checks whether an I/O error code is the one a platform reports when another process holds the file.
	/// </summary>
	/// <param name="code">The <see cref="Exception.HResult"/> of the I/O failure.</param>
	/// <param name="platform">The platform the failure happened on, or <c>null</c> when it is not known.</param>
	/// <returns><c>true</c> for a sharing or lock violation; otherwise, <c>false</c>.</returns>
	internal static bool IsLockViolation(int code, OSPlatform? platform)
	{
		if (platform == OSPlatform.Windows)
		{
			return (code & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;
		}

		if (platform == OSPlatform.Linux)
		{
			return code == LinuxWouldBlock;
		}

		if (platform == OSPlatform.OSX || platform == FreeBsd)
		{
			return code == BsdWouldBlock;
		}

		return true;
	}

	/// <summary>
	/// Describes a failure to use the PID file that does not come from another instance.
	/// </summary>
	/// <param name="operation">What was being done with the PID file.</param>
	/// <param name="failure">The failure that outlasted the retries.</param>
	/// <returns>An exception of the same kind as <paramref name="failure"/> that names the PID file.</returns>
	private static Exception PidFileFault(string operation, Exception failure)
	{
		string message = $"SingleAppInstance could not {operation} its PID file '{PidFilePath}': {failure.Message} " +
			"This is not another instance running. Check that the current user can read and write the file and its folder; " +
			"a run as another user, such as under sudo, can leave them owned by that user.";

		return failure is UnauthorizedAccessException
			? new UnauthorizedAccessException(message, failure)
			: new IOException(message, failure);
	}

	/// <summary>
	/// Gets the current process ID using the most efficient method available.
	/// </summary>
	/// <returns>The current process ID.</returns>
#if NET5_0_OR_GREATER
	private static int GetCurrentProcessId() => Environment.ProcessId;
#else
	private static int GetCurrentProcessId()
	{
		using (Process currentProc = Process.GetCurrentProcess())
		{
			return currentProc.Id;
		}
	}
#endif

	/// <summary>
	/// Parses the PID file contents and checks whether the stored process is still running.
	/// </summary>
	/// <param name="pidFileContents">The raw contents of the PID file.</param>
	/// <param name="currentPid">The current process ID.</param>
	/// <returns>What the contents say about other instances of the application.</returns>
	/// <remarks>
	/// Only the first JSON value in the file is read. A PID file torn by two older writers racing
	/// holds a complete record followed by the tail of a longer one, and that record still names the
	/// instance that wrote it.
	/// </remarks>
	private static PidFileState CheckPidFileContents(string pidFileContents, int currentPid)
	{
		ProcessInfo? storedProcess;
		try
		{
			Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(pidFileContents));
			storedProcess = JsonSerializer.Deserialize(ref reader, ProcessInfoJsonContext.Default.ProcessInfo);
			if (storedProcess == null)
			{
				return PidFileState.NoInstance;
			}
		}
		catch (JsonException)
		{
			return HandleLegacyPidFile(pidFileContents, currentPid);
		}

		if (storedProcess.ProcessId == currentPid)
		{
			return PidFileState.NoInstance;
		}

		return ToState(IsStoredProcessRunning(storedProcess));
	}

	/// <summary>
	/// Converts the outcome of a process check into a PID file state.
	/// </summary>
	/// <param name="isRunning">Whether another instance was found running.</param>
	/// <returns>The corresponding PID file state.</returns>
	private static PidFileState ToState(bool isRunning) => isRunning ? PidFileState.AnotherInstance : PidFileState.NoInstance;

	/// <summary>
	/// Handles backward-compatible legacy PID files that contain only a plain integer PID.
	/// </summary>
	/// <param name="pidFileContents">The raw contents of the PID file.</param>
	/// <param name="currentPid">The current process ID.</param>
	/// <returns>What the legacy PID says about other instances of the application.</returns>
	private static PidFileState HandleLegacyPidFile(string pidFileContents, int currentPid)
	{
		if (!int.TryParse(pidFileContents, NumberStyles.Integer, CultureInfo.InvariantCulture, out int filePid))
		{
			return PidFileState.Unreadable;
		}

		if (filePid == currentPid)
		{
			return PidFileState.NoInstance;
		}

		return ToState(IsLegacyProcessRunning(filePid));
	}

	/// <summary>
	/// Checks if the process described by the stored process info is still running
	/// and matches the expected application.
	/// </summary>
	/// <param name="storedProcess">The process information read from the PID file.</param>
	/// <returns><c>true</c> if the stored process is still running and matches; otherwise, <c>false</c>.</returns>
	private static bool IsStoredProcessRunning(ProcessInfo storedProcess) =>
		IsStoredProcessRunning(storedProcess, process => process.MainModule?.FileName);

	/// <summary>
	/// Checks if the process described by the stored process info is still running
	/// and matches the expected application, reading the main module through the given lookup.
	/// </summary>
	/// <param name="storedProcess">The process information read from the PID file.</param>
	/// <param name="readMainModuleFileName">
	/// Reads the running process's main module file name, or returns <see langword="null"/> when the
	/// module list cannot be read.
	/// </param>
	/// <returns><c>true</c> if the stored process is still running and matches; otherwise, <c>false</c>.</returns>
	/// <remarks>
	/// Windows denies another user's module list by throwing <see cref="System.ComponentModel.Win32Exception"/>.
	/// Linux builds it from <c>/proc/&lt;pid&gt;/maps</c>, which another user cannot read, and returns no
	/// main module without throwing. Both mean the module cannot be compared, so both fall back to the
	/// name and start time check.
	/// </remarks>
	internal static bool IsStoredProcessRunning(ProcessInfo storedProcess, Func<Process, string?> readMainModuleFileName)
	{
		try
		{
			using Process runningProcess = Process.GetProcessById(storedProcess.ProcessId);

			if (HasExited(runningProcess) ||
				!string.Equals(runningProcess.ProcessName, storedProcess.ProcessName, StringComparison.Ordinal))
			{
				return false;
			}

			string? mainModuleFileName = readMainModuleFileName(runningProcess);
			if (mainModuleFileName is null)
			{
				// The module list cannot be read - fall back to the name-only check
				return IsStoredProcessRunningByName(storedProcess);
			}

			return string.Equals(mainModuleFileName, storedProcess.MainModuleFileName, StringComparison.OrdinalIgnoreCase) &&
				HasStoredStartTime(runningProcess, storedProcess);
		}
		catch (ArgumentException)
		{
			// Process not found - no longer running
			return false;
		}
		catch (InvalidOperationException)
		{
			// Process has exited
			return false;
		}
		catch (System.ComponentModel.Win32Exception)
		{
			// Access denied to full process details - fall back to name-only check
			return IsStoredProcessRunningByName(storedProcess);
		}
	}

	/// <summary>
	/// Checks whether a process has exited, counting a Linux process that has exited but not yet been
	/// reaped by its parent.
	/// </summary>
	/// <param name="process">The process to check.</param>
	/// <returns><c>true</c> if the process has exited; otherwise, <c>false</c>.</returns>
	/// <remarks>
	/// A zombie keeps its <c>/proc/&lt;pid&gt;</c> entry until its parent waits for it, so
	/// <see cref="Process.HasExited"/> is <see langword="false"/> and its name still reads back, while its
	/// empty memory map leaves no main module. Without this check it would pass the name fallback and
	/// block every launch until the parent reaps it.
	/// </remarks>
	private static bool HasExited(Process process) => process.HasExited || IsLinuxZombie(process.Id);

	/// <summary>
	/// Checks whether a Linux process has exited but not yet been reaped.
	/// </summary>
	/// <param name="processId">The process ID.</param>
	/// <returns>
	/// <c>true</c> if <c>/proc/&lt;pid&gt;/stat</c> reports the zombie (<c>Z</c>) or dead (<c>X</c>) state,
	/// or the process has gone; <c>false</c> on other platforms, for a live process, or when the state cannot be read.
	/// </returns>
	private static bool IsLinuxZombie(int processId)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			return false;
		}

		string stat;
		try
		{
			stat = File.ReadAllText($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/stat");
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
		{
			// The process has gone, which is as exited as a process gets
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}

		char? state = ParseLinuxProcessState(stat);
		return state is 'Z' or 'X';
	}

	/// <summary>
	/// Reads the state field from the contents of a Linux <c>/proc/&lt;pid&gt;/stat</c> file.
	/// </summary>
	/// <param name="stat">The contents of the stat file.</param>
	/// <returns>The state character, or <see langword="null"/> when the contents cannot be parsed.</returns>
	/// <remarks>
	/// The state follows the command name, which is wrapped in parentheses and may itself contain spaces
	/// and parentheses, so it is found after the last closing parenthesis.
	/// </remarks>
	internal static char? ParseLinuxProcessState(string stat)
	{
		int commandEnd = stat.LastIndexOf(')');
		if (commandEnd < 0)
		{
			return null;
		}

		for (int i = commandEnd + 1; i < stat.Length; i++)
		{
			if (!char.IsWhiteSpace(stat[i]))
			{
				return stat[i];
			}
		}

		return null;
	}

	/// <summary>
	/// Fallback check when full process access is denied. Verifies only the process name.
	/// </summary>
	/// <param name="storedProcess">The process information read from the PID file.</param>
	/// <returns><c>true</c> if a process with the stored PID and name is running; otherwise, <c>false</c>.</returns>
	private static bool IsStoredProcessRunningByName(ProcessInfo storedProcess)
	{
		try
		{
			using Process process = Process.GetProcessById(storedProcess.ProcessId);

			return !HasExited(process) &&
				string.Equals(process.ProcessName, storedProcess.ProcessName, StringComparison.Ordinal) && HasStoredStartTime(process, storedProcess);
		}
		catch (ArgumentException)
		{
			// Process doesn't exist
			return false;
		}
		catch (InvalidOperationException)
		{
			// Process has exited
			return false;
		}
		catch (System.ComponentModel.Win32Exception)
		{
			// Access denied even for basic process info - cannot determine state
			return false;
		}
	}

	/// <summary>
	/// How far apart the stored and observed start times of one process may be, allowing for the
	/// precision lost in the JSON round trip and for platforms that derive start time from clock ticks.
	/// </summary>
	private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

	/// <summary>
	/// Checks whether a running process started when the PID file says the stored process did.
	/// </summary>
	/// <param name="runningProcess">The running process that currently holds the stored PID.</param>
	/// <param name="storedProcess">The process information read from the PID file.</param>
	/// <returns><c>true</c> if the start times match or cannot be compared; otherwise, <c>false</c>.</returns>
	/// <remarks>
	/// An application run through the shared <c>dotnet</c> host has the same process name and main module
	/// as every other process that host runs, so a stale PID recycled onto any of them would otherwise pass
	/// as another instance. The start time tells them apart. PID files written before the start time was
	/// recorded, and processes whose start time cannot be read, keep the name and module check alone.
	/// </remarks>
	private static bool HasStoredStartTime(Process runningProcess, ProcessInfo storedProcess) =>
		MatchesStoredLinuxIdentity(storedProcess, ReadLinuxIdentity(runningProcess.Id)) ??
		HasStoredStartTime(() => runningProcess.StartTime, storedProcess.StartTime);

	/// <summary>
	/// Compares a running process's Linux identity with the one stored in the PID file.
	/// </summary>
	/// <param name="storedProcess">The process information read from the PID file.</param>
	/// <param name="runningIdentity">The running process's identity, or <see langword="null"/> when it cannot be read.</param>
	/// <returns>
	/// Whether the identities match, or <see langword="null"/> when either side has none and the wall-clock
	/// start time has to be compared instead.
	/// </returns>
	/// <remarks>
	/// On Linux, .NET derives another process's <see cref="Process.StartTime"/> from the current wall clock,
	/// so it moves whenever the clock is stepped (an NTP correction, a resume from suspend), and a running
	/// instance would no longer match its own PID file. The start time in clock ticks since boot, together
	/// with the boot it counts from, names the process without depending on the wall clock.
	/// </remarks>
	internal static bool? MatchesStoredLinuxIdentity(ProcessInfo storedProcess, (long StartTicks, string BootId)? runningIdentity)
	{
		if (storedProcess.LinuxStartTicks is not long storedStartTicks ||
			string.IsNullOrEmpty(storedProcess.LinuxBootId) ||
			runningIdentity is not (long runningStartTicks, string runningBootId))
		{
			return null;
		}

		return storedStartTicks == runningStartTicks &&
			string.Equals(storedProcess.LinuxBootId, runningBootId, StringComparison.Ordinal);
	}

	/// <summary>
	/// Reads a process's start time in clock ticks since boot, and the current boot's ID, on Linux.
	/// </summary>
	/// <param name="processId">The process to read.</param>
	/// <returns>The process's identity, or <see langword="null"/> when not on Linux or it cannot be read.</returns>
	internal static (long StartTicks, string BootId)? ReadLinuxIdentity(int processId)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			return null;
		}

		try
		{
			string bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
			long? startTicks = ParseLinuxStartTicks(File.ReadAllText($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/stat"));
			return startTicks is long ticks && bootId.Length > 0 ? (ticks, bootId) : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// The process has exited, or /proc is unavailable
			return null;
		}
	}

	/// <summary>
	/// Reads the start time field (field 22) from the contents of <c>/proc/&lt;pid&gt;/stat</c>.
	/// </summary>
	/// <param name="stat">The contents of the stat file.</param>
	/// <returns>The start time in clock ticks since boot, or <see langword="null"/> when the contents can't be parsed.</returns>
	/// <remarks>
	/// Field 2 is the command name in parentheses, and may itself hold spaces and parentheses, so the
	/// fields are counted from the last closing parenthesis, after which field 3 begins.
	/// </remarks>
	internal static long? ParseLinuxStartTicks(string stat)
	{
		const int StartTimeField = 22;
		const int FirstFieldAfterCommand = 3;

		int commandEnd = stat.LastIndexOf(')');
		if (commandEnd < 0)
		{
			return null;
		}

		string[] fields = stat[(commandEnd + 1)..].Split([' '], StringSplitOptions.RemoveEmptyEntries);
		int index = StartTimeField - FirstFieldAfterCommand;
		return index < fields.Length &&
			long.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out long startTicks)
				? startTicks
				: null;
	}

	/// <summary>
	/// Checks whether a running process's start time matches the stored one.
	/// </summary>
	/// <param name="readRunningStartTime">Reads the running process's start time.</param>
	/// <param name="storedStartTime">The start time read from the PID file.</param>
	/// <returns><c>true</c> if the start times match or cannot be compared; otherwise, <c>false</c>.</returns>
	internal static bool HasStoredStartTime(Func<DateTime> readRunningStartTime, DateTime storedStartTime)
	{
		if (storedStartTime == default)
		{
			return true;
		}

		try
		{
			TimeSpan difference = readRunningStartTime().ToUniversalTime() - storedStartTime.ToUniversalTime();
			return difference.Duration() <= StartTimeTolerance;
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			// The start time is unavailable or access to it is denied
			return true;
		}
	}

	/// <summary>
	/// Checks if the PID read from a legacy PID file belongs to another running instance of this application.
	/// </summary>
	/// <param name="pid">The process ID read from the legacy PID file.</param>
	/// <returns><c>true</c> if the PID belongs to a running process with the same name as the current process; otherwise, <c>false</c>.</returns>
	/// <remarks>
	/// The legacy format stores nothing but the PID, so a stale file left behind by an abnormal exit
	/// will match whatever unrelated process the operating system later recycles that PID onto. The
	/// running process's name is therefore compared against the current process's name before it is
	/// treated as another instance, mirroring the identity confirmation the JSON format performs.
	/// When identity cannot be confirmed the PID is treated as stale so the application still launches.
	/// </remarks>
	private static bool IsLegacyProcessRunning(int pid)
	{
		string currentProcessName;
		using (Process currentProcess = Process.GetCurrentProcess())
		{
			currentProcessName = currentProcess.ProcessName;
		}

		try
		{
			using Process runningProcess = Process.GetProcessById(pid);

			return !HasExited(runningProcess) &&
				IsSameApplicationName(runningProcess.ProcessName, currentProcessName);
		}
		catch (ArgumentException)
		{
			// Process not found - no longer running
			return false;
		}
		catch (InvalidOperationException)
		{
			// Process has exited
			return false;
		}
		catch (System.ComponentModel.Win32Exception)
		{
			// Access denied to process details - identity cannot be confirmed
			return false;
		}
	}

	/// <summary>
	/// The longest process name some platforms report when asked about a process other than the current one.
	/// </summary>
	/// <remarks>Linux stores at most 15 characters of a process name, so longer names are read back truncated.</remarks>
	private const int TruncatedProcessNameLength = 15;

	/// <summary>
	/// Compares the name of another running process against the current process's own name.
	/// </summary>
	/// <param name="runningProcessName">The name reported for the other running process.</param>
	/// <param name="currentProcessName">The name reported for the current process.</param>
	/// <returns><c>true</c> if both names describe the same application; otherwise, <c>false</c>.</returns>
	/// <remarks>
	/// The current process reports its full name while another process's name can come back truncated,
	/// so a name truncated at exactly the platform limit is accepted when it prefixes the current name.
	/// </remarks>
	private static bool IsSameApplicationName(string runningProcessName, string currentProcessName)
	{
		if (string.Equals(runningProcessName, currentProcessName, StringComparison.Ordinal))
		{
			return true;
		}

		return runningProcessName.Length == TruncatedProcessNameLength &&
			currentProcessName.Length > TruncatedProcessNameLength &&
			currentProcessName.StartsWith(runningProcessName, StringComparison.Ordinal);
	}

	/// <summary>
	/// Writes the current process information to the PID file.
	/// </summary>
	/// <remarks>
	/// This method writes the current process information to the PID file in the application data path.
	/// The record is written to a temporary file beside the PID file and then moved over it, so a reader
	/// sees either the previous PID file or the new one and never a partial or interleaved write.
	/// Replacing the file is retried briefly while another instance holds it.
	/// </remarks>
	/// <exception cref="IOException">The PID file stayed in use for every attempt.</exception>
	/// <exception cref="UnauthorizedAccessException">The PID file could not be replaced.</exception>
	internal static void WritePidFile()
	{
		Directory.CreateDirectory(PidDirectoryPath);

		using Process currentProcess = Process.GetCurrentProcess();
		ProcessInfo processInfo = new()
		{
			ProcessId = currentProcess.Id,
			ProcessName = currentProcess.ProcessName,
			StartTime = currentProcess.StartTime,
			MainModuleFileName = currentProcess.MainModule?.FileName
		};

		if (ReadLinuxIdentity(currentProcess.Id) is (long startTicks, string bootId))
		{
			processInfo.LinuxStartTicks = startTicks;
			processInfo.LinuxBootId = bootId;
		}

		string json = JsonSerializer.Serialize(processInfo, ProcessInfoJsonContext.Default.ProcessInfo);
		string pidFilePath = PidFilePath;
		string temporaryPath = $"{pidFilePath}.{Guid.NewGuid():N}.tmp";

		try
		{
			File.WriteAllText(temporaryPath, json);

			for (int attempt = 1; attempt < PidFileAttempts; attempt++)
			{
				try
				{
					ReplacePidFile(temporaryPath, pidFilePath);
					return;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					// Another instance is reading or replacing the PID file
				}

				Thread.Sleep(TimeSpan.FromTicks(PidFileRetryDelay.Ticks * attempt));
			}

			// The final attempt lets a persistent failure reach the caller
			ReplacePidFile(temporaryPath, pidFilePath);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}

	/// <summary>
	/// Writes the current process information to the PID file, reporting failure instead of throwing.
	/// </summary>
	/// <returns><c>true</c> if the PID file now describes this process; otherwise, <c>false</c>.</returns>
	/// <remarks>
	/// The PID file stays locked when another instance keeps it busy for the whole retry window,
	/// and this instance cannot claim it. Any other failure, such as access being denied, is a
	/// lasting fault rather than another instance, and is thrown.
	/// </remarks>
	/// <exception cref="IOException">The PID file or its folder cannot be written for a reason other than a lock.</exception>
	/// <exception cref="UnauthorizedAccessException">Access to the PID file or its folder is denied.</exception>
	internal static bool TryWritePidFile()
	{
		try
		{
			WritePidFile();
			return true;
		}
		catch (IOException ex) when (IsLockViolation(ex))
		{
			return false;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw PidFileFault("write", ex);
		}
	}

	/// <summary>
	/// Moves a fully written temporary file over the PID file in a single operation.
	/// </summary>
	/// <param name="temporaryPath">The fully written temporary file.</param>
	/// <param name="pidFilePath">The PID file to create or replace.</param>
#if NETCOREAPP3_0_OR_GREATER
	private static void ReplacePidFile(string temporaryPath, string pidFilePath) =>
		File.Move(temporaryPath, pidFilePath, overwrite: true);
#else
	private static void ReplacePidFile(string temporaryPath, string pidFilePath)
	{
		if (File.Exists(pidFilePath))
		{
			File.Replace(temporaryPath, pidFilePath, destinationBackupFileName: null);
		}
		else
		{
			File.Move(temporaryPath, pidFilePath);
		}
	}
#endif
}
