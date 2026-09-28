// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SingleAppInstance;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using ktsu.AppDataStorage;
using ktsu.Semantics.Paths;
using ktsu.Semantics.Strings;

/// <summary>
/// Provides a mechanism to ensure that only one instance of an application is running at a time.
/// </summary>
public static class SingleAppInstance
{
	internal static AbsoluteDirectoryPath PidDirectoryPath { get; } = AppData.Path;
	internal static AbsoluteFilePath PidFilePath { get; } = PidDirectoryPath / $".{nameof(SingleAppInstance)}.pid".As<FileName>();

	/// <summary>
	/// Exits the application if another instance is already running.
	/// </summary>
	/// <remarks>
	/// This method checks if another instance of the application is already running by calling <see cref="ShouldLaunch"/>.
	/// If another instance is detected, the current application exits with a status code of 0.
	/// </remarks>
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
	/// If the PID file stays locked by another process, or cannot be written, another instance
	/// is taken to be starting and this method returns <c>false</c> rather than throwing.
	/// </remarks>
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
	/// stays inaccessible it is treated as another instance that is starting.
	/// </remarks>
	internal static PidFileState ReadPidFileState()
	{
		int currentPid = GetCurrentProcessId();

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
				// Another instance is writing or holding the PID file
			}

			if (attempt < PidFileAttempts)
			{
				Thread.Sleep(TimeSpan.FromTicks(PidFileRetryDelay.Ticks * attempt));
			}
		}

		// The PID file stayed inaccessible, so another instance is holding it while it starts
		return PidFileState.AnotherInstance;
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
			storedProcess = JsonSerializer.Deserialize<ProcessInfo>(ref reader);
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
	private static bool IsStoredProcessRunning(ProcessInfo storedProcess)
	{
		try
		{
			using Process runningProcess = Process.GetProcessById(storedProcess.ProcessId);

			return !runningProcess.HasExited &&
				string.Equals(runningProcess.ProcessName, storedProcess.ProcessName, StringComparison.Ordinal) &&
				runningProcess.MainModule != null &&
				string.Equals(runningProcess.MainModule.FileName, storedProcess.MainModuleFileName, StringComparison.OrdinalIgnoreCase) &&
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
	/// Fallback check when full process access is denied. Verifies only the process name.
	/// </summary>
	/// <param name="storedProcess">The process information read from the PID file.</param>
	/// <returns><c>true</c> if a process with the stored PID and name is running; otherwise, <c>false</c>.</returns>
	private static bool IsStoredProcessRunningByName(ProcessInfo storedProcess)
	{
		try
		{
			using Process process = Process.GetProcessById(storedProcess.ProcessId);

			return !process.HasExited &&
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
		HasStoredStartTime(() => runningProcess.StartTime, storedProcess.StartTime);

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

			return !runningProcess.HasExited &&
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

		string json = JsonSerializer.Serialize(processInfo);
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
	/// The PID file stays in use when another instance keeps it busy for the whole retry window,
	/// and cannot be replaced at all when access is denied. Either way this instance cannot claim it.
	/// </remarks>
	internal static bool TryWritePidFile()
	{
		try
		{
			WritePidFile();
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
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
