// Copyright (c) ktsu.dev
// All rights reserved.
// Licensed under the MIT license.

namespace ktsu.SingleAppInstance;

using System.Diagnostics;
using System.Globalization;
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
		WritePidFile();
		Thread.Sleep(1000);

		// in case there was a race and another instance is starting at the same time we
		// need to check again to see if we won the lock
		return !IsAlreadyRunning();
	}

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
	internal static bool IsAlreadyRunning()
	{
		int currentPid = GetCurrentProcessId();

		try
		{
			string pidFileContents = File.ReadAllText(PidFilePath);
			return CheckPidFileContents(pidFileContents, currentPid);
		}
		catch (DirectoryNotFoundException)
		{
			// PID directory doesn't exist yet - no instance running
		}
		catch (FileNotFoundException)
		{
			// PID file doesn't exist - no instance running
		}
		catch (FormatException)
		{
			// PID file content is corrupted - treat as no instance running
		}

		return false;
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
	/// <returns><c>true</c> if a different instance of the application is running; otherwise, <c>false</c>.</returns>
	private static bool CheckPidFileContents(string pidFileContents, int currentPid)
	{
		ProcessInfo? storedProcess;
		try
		{
			storedProcess = JsonSerializer.Deserialize<ProcessInfo>(pidFileContents);
			if (storedProcess == null)
			{
				return false;
			}
		}
		catch (JsonException)
		{
			return HandleLegacyPidFile(pidFileContents, currentPid);
		}

		if (storedProcess.ProcessId == currentPid)
		{
			return false;
		}

		return IsStoredProcessRunning(storedProcess);
	}

	/// <summary>
	/// Handles backward-compatible legacy PID files that contain only a plain integer PID.
	/// </summary>
	/// <param name="pidFileContents">The raw contents of the PID file.</param>
	/// <param name="currentPid">The current process ID.</param>
	/// <returns><c>true</c> if the legacy PID corresponds to a running process; otherwise, <c>false</c>.</returns>
	private static bool HandleLegacyPidFile(string pidFileContents, int currentPid)
	{
		if (!int.TryParse(pidFileContents, NumberStyles.Integer, CultureInfo.InvariantCulture, out int filePid))
		{
			return false;
		}

		if (filePid == currentPid)
		{
			return false;
		}

		return IsProcessRunning(filePid);
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
				string.Equals(runningProcess.MainModule.FileName, storedProcess.MainModuleFileName, StringComparison.OrdinalIgnoreCase);
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
				string.Equals(process.ProcessName, storedProcess.ProcessName, StringComparison.Ordinal);
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
	/// Checks if a process with the given PID is currently running.
	/// </summary>
	/// <param name="pid">The process ID to check.</param>
	/// <returns><c>true</c> if a process with the given PID is running; otherwise, <c>false</c>.</returns>
	private static bool IsProcessRunning(int pid)
	{
		Process[] processes = Process.GetProcesses();
		try
		{
			return Array.Exists(processes, p => p.Id == pid);
		}
		finally
		{
			foreach (Process p in processes)
			{
				p.Dispose();
			}
		}
	}

	/// <summary>
	/// Writes the current process information to the PID file.
	/// </summary>
	/// <remarks>
	/// This method writes the current process information to the PID file in the application data path.
	/// </remarks>
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
		File.WriteAllText(PidFilePath, json);
	}
}
