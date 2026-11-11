// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SingleAppInstance.Test;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

[TestClass]
[DoNotParallelize]
public class SingleAppInstanceTests
{
	public TestContext TestContext { get; set; } = null!;

	[TestInitialize]
	public void TestInitialize()
	{
		// Ensure the PID directory exists and the file is deleted before each test
		string pidFilePath = SingleAppInstance.PidFilePath;
		Directory.CreateDirectory(SingleAppInstance.PidDirectoryPath);
		if (Directory.Exists(pidFilePath))
		{
			Directory.Delete(pidFilePath, recursive: true);
		}

		File.Delete(pidFilePath);
	}

	[TestMethod]
	public void WritePidFile_ShouldCreateFileWithCurrentProcessInfo()
	{
		// Arrange
		string pidFilePath = SingleAppInstance.PidFilePath;

		// Act
		SingleAppInstance.WritePidFile();

		// Assert
		Assert.IsTrue(File.Exists(pidFilePath), "PID file should be created after calling WritePidFile");

		// Verify file content
		string fileContent = File.ReadAllText(pidFilePath);
		ProcessInfo? processInfo = JsonSerializer.Deserialize(fileContent, TestJsonContext.Default.ProcessInfo);

		Assert.IsNotNull(processInfo);
		using Process currentProcess = Process.GetCurrentProcess();
		Assert.AreEqual(Environment.ProcessId, processInfo.ProcessId);
		Assert.AreEqual(currentProcess.ProcessName, processInfo.ProcessName);
		Assert.IsTrue(processInfo.StartTime > DateTime.MinValue, "StartTime should be set");
	}

	[TestMethod]
	public void WritePidFile_ShouldCreateDirectoryIfNotExists()
	{
		// Arrange - directory already exists from TestInitialize, but verify file is created
		string pidFilePath = SingleAppInstance.PidFilePath;

		// Act
		SingleAppInstance.WritePidFile();

		// Assert
		Assert.IsTrue(File.Exists(pidFilePath));
		string content = File.ReadAllText(pidFilePath);
		Assert.IsFalse(string.IsNullOrEmpty(content), "PID file should contain JSON content");
	}

	[TestMethod]
	public void WritePidFile_ShouldOverwriteExistingFile()
	{
		// Arrange
		string pidFilePath = SingleAppInstance.PidFilePath;
		File.WriteAllText(pidFilePath, "old content");

		// Act
		SingleAppInstance.WritePidFile();

		// Assert
		string content = File.ReadAllText(pidFilePath);
		Assert.AreNotEqual("old content", content);
		ProcessInfo? processInfo = JsonSerializer.Deserialize(content, TestJsonContext.Default.ProcessInfo);
		Assert.IsNotNull(processInfo);
		Assert.AreEqual(Environment.ProcessId, processInfo.ProcessId);
	}

	[TestMethod]
	public void IsAlreadyRunning_WhenPidFileDoesNotExist_ShouldReturnFalse()
	{
		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result);
	}

	[TestMethod]
	public void IsAlreadyRunning_WithCurrentProcessId_ShouldReturnFalse()
	{
		// Arrange
		string pidFilePath = SingleAppInstance.PidFilePath;

		using Process currentProcess = Process.GetCurrentProcess();
		ProcessInfo processInfo = new()
		{
			ProcessId = currentProcess.Id,
			ProcessName = currentProcess.ProcessName,
			StartTime = currentProcess.StartTime,
			MainModuleFileName = currentProcess.MainModule?.FileName,
		};

		string json = JsonSerializer.Serialize(processInfo, TestJsonContext.Default.ProcessInfo);
		File.WriteAllText(pidFilePath, json);

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result);
	}

	[TestMethod]
	public void IsAlreadyRunning_WithLegacyPidFile_CurrentProcess_ShouldReturnFalse()
	{
		// Arrange - legacy format is just a plain integer PID
		string pidFilePath = SingleAppInstance.PidFilePath;
		int currentPid = Environment.ProcessId;
		File.WriteAllText(pidFilePath, currentPid.ToString(CultureInfo.InvariantCulture));

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result, "Should return false because it's the current process");
	}

	[TestMethod]
	public void IsAlreadyRunning_WithLegacyPidFile_NonExistentProcess_ShouldReturnFalse()
	{
		// Arrange - legacy PID file with a PID that doesn't correspond to any running process
		string pidFilePath = SingleAppInstance.PidFilePath;
		File.WriteAllText(pidFilePath, "99999999");

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result, "Should return false for a non-existent process in legacy format");
	}

	[TestMethod]
	public void IsAlreadyRunning_WithLegacyPidFile_RunningProcessWithDifferentName_ShouldReturnFalse()
	{
		// Arrange - legacy PID file holding the PID of an unrelated running process, which is what a
		// stale legacy PID file looks like once the operating system has recycled the PID
		string pidFilePath = SingleAppInstance.PidFilePath;
		using Process currentProcess = Process.GetCurrentProcess();
		using Process? targetProcess = FindRunningProcessWithDifferentName(currentProcess);

		Assert.IsNotNull(targetProcess, "Should find at least one other running process with a different name");
		File.WriteAllText(pidFilePath, targetProcess.Id.ToString(CultureInfo.InvariantCulture));

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result, "Should return false when the recycled PID belongs to a process that is not this application");
	}

	[TestMethod]
	public void IsAlreadyRunning_WithLegacyPidFile_RunningProcessWithSameName_ShouldReturnTrue()
	{
		// Arrange - legacy PID file holding the PID of a running process that carries this
		// application's own process name, which is as close to another instance as the legacy
		// format can describe
		string pidFilePath = SingleAppInstance.PidFilePath;
		using HelperProcess helper = StartInstanceLookalikeProcess();

		File.WriteAllText(pidFilePath, helper.Process.Id.ToString(CultureInfo.InvariantCulture));

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsTrue(result, "Should return true for a running process that matches this application's name in legacy format");
	}

	[TestMethod]
	public void IsAlreadyRunning_WithNonExistentPid_ShouldReturnFalse()
	{
		// Arrange - JSON PID file with PID -1 which doesn't exist
		string pidFilePath = SingleAppInstance.PidFilePath;
		ProcessInfo processInfo = new()
		{
			ProcessId = -1,
			ProcessName = "NonExistentProcess",
			StartTime = DateTime.Now,
			MainModuleFileName = "NonExistentFile.exe",
		};

		string json = JsonSerializer.Serialize(processInfo, TestJsonContext.Default.ProcessInfo);
		File.WriteAllText(pidFilePath, json);

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result);
	}

	[TestMethod]
	public void IsAlreadyRunning_WithInvalidJsonInPidFile_ShouldHandleGracefully()
	{
		// Arrange - content that is not valid JSON and not a valid integer
		string pidFilePath = SingleAppInstance.PidFilePath;
		File.WriteAllText(pidFilePath, "This is not valid JSON");

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result, "Should handle the error gracefully and return false");
	}

	[TestMethod]
	public void IsAlreadyRunning_WithEmptyPidFile_ShouldHandleGracefully()
	{
		// Arrange
		string pidFilePath = SingleAppInstance.PidFilePath;
		File.WriteAllText(pidFilePath, string.Empty);

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result, "Should handle the error gracefully and return false");
	}

	[TestMethod]
	public void IsAlreadyRunning_WithNullJson_ShouldReturnFalse()
	{
		// Arrange - JSON "null" deserializes to null ProcessInfo
		string pidFilePath = SingleAppInstance.PidFilePath;
		File.WriteAllText(pidFilePath, "null");

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result, "Should return false when JSON deserializes to null");
	}

	[TestMethod]
	public void IsAlreadyRunning_WithRunningProcessButDifferentName_ShouldReturnFalse()
	{
		// Arrange - use a real running process PID but with a wrong process name
		string pidFilePath = SingleAppInstance.PidFilePath;
		Process? targetProcess = null;

		try
		{
			// Find a different running process
			foreach (Process p in Process.GetProcesses())
			{
				if (p.Id != Environment.ProcessId)
				{
					targetProcess = p;
					break;
				}
				else
				{
					p.Dispose();
				}
			}

			Assert.IsNotNull(targetProcess, "Should find at least one other running process");

			ProcessInfo processInfo = new()
			{
				ProcessId = targetProcess.Id,
				ProcessName = "CompletelyWrongName_" + Guid.NewGuid().ToString("N"),
				StartTime = DateTime.Now,
				MainModuleFileName = "C:\\nonexistent\\fake.exe",
			};

			string json = JsonSerializer.Serialize(processInfo, TestJsonContext.Default.ProcessInfo);
			File.WriteAllText(pidFilePath, json);

			// Act
			bool result = SingleAppInstance.IsAlreadyRunning();

			// Assert
			Assert.IsFalse(result, "Should return false when process name doesn't match");
		}
		finally
		{
			targetProcess?.Dispose();
		}
	}

	[TestMethod]
	public void IsAlreadyRunning_WithMatchingProcessButDifferentStartTime_ShouldReturnFalse()
	{
		// Arrange - the PID, name and main module all describe a live process, but the start time
		// does not, which is what a stale PID file looks like once the operating system recycles the
		// PID onto another process run by the same host, such as another `dotnet` process
		string pidFilePath = SingleAppInstance.PidFilePath;
		using HelperProcess helper = HelperProcess.Start("StartTimeHelper");

		ProcessInfo processInfo = DescribeProcess(helper.Process);
		processInfo.StartTime = processInfo.StartTime.AddHours(-1);
		File.WriteAllText(pidFilePath, JsonSerializer.Serialize(processInfo, TestJsonContext.Default.ProcessInfo));

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result, "Should return false when the running process started at a different time than the stored one");
	}

	[TestMethod]
	public void IsAlreadyRunning_WithMatchingProcessAndStartTime_ShouldReturnTrue()
	{
		// Arrange - every stored field describes the live process, so it is another instance
		string pidFilePath = SingleAppInstance.PidFilePath;
		using HelperProcess helper = HelperProcess.Start("StartTimeHelper");

		File.WriteAllText(pidFilePath, JsonSerializer.Serialize(DescribeProcess(helper.Process), TestJsonContext.Default.ProcessInfo));

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsTrue(result, "Should return true when the stored process information matches a running process");
	}

	[TestMethod]
	public void IsAlreadyRunning_WithMatchingProcessAndNoStoredStartTime_ShouldReturnTrue()
	{
		// Arrange - a PID file written before the start time was recorded deserializes it as default
		string pidFilePath = SingleAppInstance.PidFilePath;
		using HelperProcess helper = HelperProcess.Start("StartTimeHelper");

		ProcessInfo processInfo = DescribeProcess(helper.Process);
		processInfo.StartTime = default;
		File.WriteAllText(pidFilePath, JsonSerializer.Serialize(processInfo, TestJsonContext.Default.ProcessInfo));

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsTrue(result, "Should fall back to the name and module check when no start time was stored");
	}

	[TestMethod]
	public void IsStoredProcessRunning_WhenMainModuleIsUnreadable_ShouldFallBackToNameAndStartTime()
	{
		// Arrange - on Linux another user's MainModule is null rather than a Win32Exception
		using HelperProcess helper = HelperProcess.Start("StartTimeHelper");
		SingleAppInstance.ProcessInfo storedProcess = DescribeProcessForLibrary(helper.Process);

		// Act
		bool result = SingleAppInstance.IsStoredProcessRunning(storedProcess, _ => null);

		// Assert
		Assert.IsTrue(result, "A process whose module list can't be read but whose name and start time match is another instance");
	}

	[TestMethod]
	public void IsStoredProcessRunning_WhenMainModuleIsUnreadableAndStartTimeDiffers_ShouldReturnFalse()
	{
		// Arrange
		using HelperProcess helper = HelperProcess.Start("StartTimeHelper");
		SingleAppInstance.ProcessInfo storedProcess = DescribeProcessForLibrary(helper.Process);
		storedProcess.StartTime = helper.Process.StartTime.AddMinutes(-5);

		// Act
		bool result = SingleAppInstance.IsStoredProcessRunning(storedProcess, _ => null);

		// Assert
		Assert.IsFalse(result, "The fallback still compares the start time, so a recycled PID is not another instance");
	}

	[TestMethod]
	public void IsStoredProcessRunning_WhenMainModuleAccessIsDenied_ShouldFallBackToNameAndStartTime()
	{
		// Arrange - the Windows path, which throws instead of returning null
		using HelperProcess helper = HelperProcess.Start("StartTimeHelper");
		SingleAppInstance.ProcessInfo storedProcess = DescribeProcessForLibrary(helper.Process);

		// Act
		bool result = SingleAppInstance.IsStoredProcessRunning(storedProcess, _ => throw new Win32Exception());

		// Assert
		Assert.IsTrue(result, "Access denied to the module list should still fall back to the name and start time check");
	}

	[TestMethod]
	public void IsStoredProcessRunning_WhenMainModuleDiffers_ShouldReturnFalse()
	{
		// Arrange
		using HelperProcess helper = HelperProcess.Start("StartTimeHelper");
		SingleAppInstance.ProcessInfo storedProcess = DescribeProcessForLibrary(helper.Process);

		// Act
		bool result = SingleAppInstance.IsStoredProcessRunning(storedProcess, _ => "/some/other/app");

		// Assert
		Assert.IsFalse(result, "A readable main module that differs from the stored one is not another instance");
	}

	[TestMethod]
	public void IsAlreadyRunning_WhenWallClockWasSteppedAfterTheInstanceStarted_ShouldReturnTrue()
	{
		// Arrange - on Linux the running process's StartTime is the wall clock minus its uptime, so an NTP
		// step or a resume moves it away from the value stored at launch. The stored Linux identity doesn't move.
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			Assert.Inconclusive("The wall clock only moves another process's StartTime on Linux");
		}

		string pidFilePath = SingleAppInstance.PidFilePath;
		using HelperProcess helper = HelperProcess.Start("StartTimeHelper");

		ProcessInfo processInfo = DescribeProcessWithLinuxIdentity(helper.Process);
		processInfo.StartTime = processInfo.StartTime.AddSeconds(-5);
		File.WriteAllText(pidFilePath, JsonSerializer.Serialize(processInfo, TestJsonContext.Default.ProcessInfo));

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsTrue(result, "A wall-clock step must not make a running instance look like a recycled PID");
	}

	[TestMethod]
	public void IsAlreadyRunning_WhenLinuxStartTicksDiffer_ShouldReturnFalse()
	{
		// Arrange - the wall-clock start time matches, but the process holding the PID started at a different
		// tick, which is a PID recycled onto another process run by the same host
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			Assert.Inconclusive("The Linux identity is only recorded on Linux");
		}

		string pidFilePath = SingleAppInstance.PidFilePath;
		using HelperProcess helper = HelperProcess.Start("StartTimeHelper");

		ProcessInfo processInfo = DescribeProcessWithLinuxIdentity(helper.Process);
		processInfo.LinuxStartTicks--;
		File.WriteAllText(pidFilePath, JsonSerializer.Serialize(processInfo, TestJsonContext.Default.ProcessInfo));

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result, "Should return false when the process holding the PID started at a different tick");
	}

	[TestMethod]
	public void IsAlreadyRunning_WhenLinuxBootIdDiffers_ShouldReturnFalse()
	{
		// Arrange - a PID file from a previous boot can name a start tick that a process of this boot shares
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			Assert.Inconclusive("The Linux identity is only recorded on Linux");
		}

		string pidFilePath = SingleAppInstance.PidFilePath;
		using HelperProcess helper = HelperProcess.Start("StartTimeHelper");

		ProcessInfo processInfo = DescribeProcessWithLinuxIdentity(helper.Process);
		processInfo.LinuxBootId = Guid.NewGuid().ToString();
		File.WriteAllText(pidFilePath, JsonSerializer.Serialize(processInfo, TestJsonContext.Default.ProcessInfo));

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result, "Should return false when the PID file was written in a different boot");
	}

	[TestMethod]
	public void WritePidFile_OnLinux_ShouldRecordTheClockIndependentIdentity()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			Assert.Inconclusive("The Linux identity is only recorded on Linux");
		}

		SingleAppInstance.WritePidFile();

		ProcessInfo? processInfo = JsonSerializer.Deserialize(File.ReadAllText(SingleAppInstance.PidFilePath), TestJsonContext.Default.ProcessInfo);
		Assert.IsNotNull(processInfo);
		using Process currentProcess = Process.GetCurrentProcess();
		ProcessInfo expected = DescribeProcessWithLinuxIdentity(currentProcess);
		Assert.AreEqual(expected.LinuxStartTicks, processInfo.LinuxStartTicks);
		Assert.AreEqual(expected.LinuxBootId, processInfo.LinuxBootId);
	}

	[TestMethod]
	public void MatchesStoredLinuxIdentity_ComparesBothFieldsAndDefersWhenEitherSideHasNone()
	{
		// A stored identity with no running identity to compare falls back to the wall-clock check
		SingleAppInstance.ProcessInfo stored = new() { LinuxStartTicks = 42, LinuxBootId = "boot" };

		Assert.IsNull(SingleAppInstance.MatchesStoredLinuxIdentity(stored, null));
		Assert.IsNull(SingleAppInstance.MatchesStoredLinuxIdentity(new SingleAppInstance.ProcessInfo(), (42, "boot")));
		Assert.IsTrue(SingleAppInstance.MatchesStoredLinuxIdentity(stored, (42, "boot")));
		Assert.IsFalse(SingleAppInstance.MatchesStoredLinuxIdentity(stored, (43, "boot")));
		Assert.IsFalse(SingleAppInstance.MatchesStoredLinuxIdentity(stored, (42, "other")));
	}

	[TestMethod]
	[DataRow("1234 (sleep) S 1 1234 1234 0 -1 4194560 100 0 0 0 0 0 0 0 20 0 1 0 987654 8192000 200", 987654L)]
	[DataRow("1234 (a) b (c)) R 1 1234 1234 0 -1 4194560 100 0 0 0 0 0 0 0 20 0 1 0 55 8192000 200", 55L)]
	[DataRow("1234 (short) S 1 2", null)]
	[DataRow("no command name", null)]
	public void ParseLinuxStartTicks_ReadsFieldTwentyTwoAfterTheCommandName(string stat, long? expected) =>
		Assert.AreEqual(expected, SingleAppInstance.ParseLinuxStartTicks(stat));

	[TestMethod]
	public void HasStoredStartTime_WithinTolerance_ShouldReturnTrue()
	{
		DateTime stored = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

		Assert.IsTrue(SingleAppInstance.HasStoredStartTime(() => stored.AddMilliseconds(900), stored));
		Assert.IsTrue(SingleAppInstance.HasStoredStartTime(() => stored.AddMilliseconds(-900), stored));
	}

	[TestMethod]
	public void HasStoredStartTime_BeyondTolerance_ShouldReturnFalse()
	{
		DateTime stored = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

		Assert.IsFalse(SingleAppInstance.HasStoredStartTime(() => stored.AddSeconds(2), stored));
		Assert.IsFalse(SingleAppInstance.HasStoredStartTime(() => stored.AddSeconds(-2), stored));
	}

	[TestMethod]
	public void HasStoredStartTime_ComparesLocalAndUniversalTimesAsTheSameInstant()
	{
		DateTime stored = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

		Assert.IsTrue(SingleAppInstance.HasStoredStartTime(stored.ToLocalTime, stored));
	}

	[TestMethod]
	public void HasStoredStartTime_WithNoStoredStartTime_ShouldNotReadTheRunningStartTime()
	{
		Assert.IsTrue(SingleAppInstance.HasStoredStartTime(() => throw new AssertFailedException("The running start time should not be read"), default));
	}

	[TestMethod]
	public void HasStoredStartTime_WhenRunningStartTimeIsUnavailable_ShouldReturnTrue()
	{
		DateTime stored = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

		Assert.IsTrue(SingleAppInstance.HasStoredStartTime(() => throw new InvalidOperationException(), stored));
		Assert.IsTrue(SingleAppInstance.HasStoredStartTime(() => throw new Win32Exception(), stored));
	}

	[TestMethod]
	public void IsAlreadyRunning_WithHighNonExistentPid_ShouldReturnFalse()
	{
		// Arrange - JSON PID file with a very high PID that shouldn't exist
		string pidFilePath = SingleAppInstance.PidFilePath;
		ProcessInfo processInfo = new()
		{
			ProcessId = int.MaxValue,
			ProcessName = "FakeProcess",
			StartTime = DateTime.Now,
			MainModuleFileName = "fake.exe",
		};

		string json = JsonSerializer.Serialize(processInfo, TestJsonContext.Default.ProcessInfo);
		File.WriteAllText(pidFilePath, json);

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result, "Should return false when the process doesn't exist");
	}

	[TestMethod]
	public void IsAlreadyRunning_WithEmptyJsonObject_ShouldReturnFalse()
	{
		// Arrange - empty JSON object deserializes to ProcessInfo with default values (PID=0)
		string pidFilePath = SingleAppInstance.PidFilePath;
		File.WriteAllText(pidFilePath, "{}");

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result);
	}

	[TestMethod]
	public void IsAlreadyRunning_WithZeroPid_ShouldReturnFalse()
	{
		// Arrange - PID 0 is the System Idle Process, but our process name won't match
		string pidFilePath = SingleAppInstance.PidFilePath;
		ProcessInfo processInfo = new()
		{
			ProcessId = 0,
			ProcessName = "DefinitelyNotSystemIdle",
			StartTime = DateTime.Now,
			MainModuleFileName = "fake.exe",
		};

		string json = JsonSerializer.Serialize(processInfo, TestJsonContext.Default.ProcessInfo);
		File.WriteAllText(pidFilePath, json);

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result);
	}

	[TestMethod]
	public void ShouldLaunch_WithNoExistingInstance_ShouldReturnTrue()
	{
		// Arrange
		string pidFilePath = SingleAppInstance.PidFilePath;
		Assert.IsFalse(File.Exists(pidFilePath), "PID file should not exist at test start");

		// Act
		bool result = SingleAppInstance.ShouldLaunch();

		// Assert
		Assert.IsTrue(result, "ShouldLaunch should return true when no previous instance was running");
		Assert.IsTrue(File.Exists(pidFilePath), "PID file should be created by ShouldLaunch");

		// Verify the PID file content matches the current process
		string fileContent = File.ReadAllText(pidFilePath);
		ProcessInfo? processInfo = JsonSerializer.Deserialize(fileContent, TestJsonContext.Default.ProcessInfo);
		Assert.IsNotNull(processInfo);
		Assert.AreEqual(Environment.ProcessId, processInfo.ProcessId);
	}

	[TestMethod]
	public void ShouldLaunch_WhenAlreadyRunning_ShouldReturnFalse()
	{
		// Arrange - a running process carrying this application's name, recorded in the legacy
		// format, is what another instance looks like to IsAlreadyRunning()
		string pidFilePath = SingleAppInstance.PidFilePath;
		using HelperProcess helper = StartInstanceLookalikeProcess();

		File.WriteAllText(pidFilePath, helper.Process.Id.ToString(CultureInfo.InvariantCulture));

		// Act
		bool result = SingleAppInstance.ShouldLaunch();

		// Assert
		Assert.IsFalse(result, "ShouldLaunch should return false when another instance is detected");
	}

	[TestMethod]
	public void IsAlreadyRunning_WithGarbageSuffixedPidFileForRunningInstance_ShouldReturnTrue()
	{
		// Arrange - two writers racing leave the shorter record followed by the tail of the longer
		// one, and the record still describes the live instance that wrote it
		string pidFilePath = SingleAppInstance.PidFilePath;
		using HelperProcess helper = HelperProcess.Start("TornPidHelper");

		File.WriteAllText(pidFilePath, JsonSerializer.Serialize(DescribeProcess(helper.Process), TestJsonContext.Default.ProcessInfo) + "0\"}");

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsTrue(result, "A torn PID file whose leading record describes a running instance should read as that instance");
	}

	[TestMethod]
	public void ShouldLaunch_WithGarbageSuffixedPidFileForRunningInstance_ShouldReturnFalse()
	{
		// Arrange
		string pidFilePath = SingleAppInstance.PidFilePath;
		using HelperProcess helper = HelperProcess.Start("TornPidHelper");

		File.WriteAllText(pidFilePath, JsonSerializer.Serialize(DescribeProcess(helper.Process), TestJsonContext.Default.ProcessInfo) + "0\"}");

		// Act
		bool result = SingleAppInstance.ShouldLaunch();

		// Assert
		Assert.IsFalse(result, "A torn PID file must not let a second instance launch while the first is running");
	}

	[TestMethod]
	public void ShouldLaunch_WhenPidFileBecomesUnreadableDuringRaceWindow_ShouldReturnFalse()
	{
		// Arrange - once this instance has written its PID file, a racing instance leaves content
		// that cannot be parsed, which must count as that instance rather than as no instance
		string pidFilePath = SingleAppInstance.PidFilePath;
		string ownPid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

		Task racingWriter = Task.Run(async () =>
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
			{
				try
				{
					string content = await File.ReadAllTextAsync(pidFilePath, TestContext.CancellationToken).ConfigureAwait(false);
					if (content.Contains(ownPid, StringComparison.Ordinal))
					{
						await File.WriteAllTextAsync(pidFilePath, "{\"ProcessId\":12", TestContext.CancellationToken).ConfigureAwait(false);
						return;
					}
				}
				catch (IOException)
				{
					// Not written yet, or being replaced
				}
				catch (UnauthorizedAccessException)
				{
					// Being replaced
				}

				await Task.Delay(10, TestContext.CancellationToken).ConfigureAwait(false);
			}
		}, TestContext.CancellationToken);

		// Act
		bool result = SingleAppInstance.ShouldLaunch();
		racingWriter.Wait(TestContext.CancellationToken);

		// Assert
		Assert.IsFalse(result, "Unparseable content after this instance wrote its PID file should not grant a launch");
	}

	[TestMethod]
	public void ShouldLaunch_WhenPidFileIsHeldExclusively_ShouldReturnFalseWithoutThrowing()
	{
		// Arrange - another instance holding the PID file open is what a simultaneous launch looks like
		string pidFilePath = SingleAppInstance.PidFilePath;
		using FileStream heldPidFile = new(pidFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

		// Act
		bool result = SingleAppInstance.ShouldLaunch();

		// Assert
		Assert.IsFalse(result, "A PID file held by another instance should read as that instance starting");
	}

	[TestMethod]
	public void WritePidFile_WhileBeingRead_ReaderNeverSeesAPartialFile()
	{
		// Arrange - a reader racing a writer must only ever see a whole PID file
		string pidFilePath = SingleAppInstance.PidFilePath;
		SingleAppInstance.WritePidFile();

		using CancellationTokenSource writing = new();
		Task writer = Task.Run(() =>
		{
			while (!writing.IsCancellationRequested)
			{
				try
				{
					SingleAppInstance.WritePidFile();
				}
				catch (IOException)
				{
					// The reader held the file for every retry; contention is expected here
				}
				catch (UnauthorizedAccessException)
				{
					// The reader held the file for every retry; contention is expected here
				}
			}
		}, TestContext.CancellationToken);

		int partialReads = 0;
		string? lastPartialContent = null;

		// Act
		try
		{
			for (int i = 0; i < 2000; i++)
			{
				string content;
				try
				{
					content = File.ReadAllText(pidFilePath);
				}
				catch (IOException)
				{
					// The file was being replaced; a sharing violation is not a partial read
					continue;
				}
				catch (UnauthorizedAccessException)
				{
					// The file was being replaced; a pending delete is not a partial read
					continue;
				}

				try
				{
					if (JsonSerializer.Deserialize(content, TestJsonContext.Default.ProcessInfo) is null)
					{
						partialReads++;
						lastPartialContent = content;
					}
				}
				catch (JsonException)
				{
					partialReads++;
					lastPartialContent = content;
				}
			}
		}
		finally
		{
			writing.Cancel();
			writer.Wait(TestContext.CancellationToken);
		}

		// Assert
		Assert.AreEqual(0, partialReads, $"Every read should see a whole PID file; last partial content was '{lastPartialContent}'");
	}

	[TestMethod]
	public void WritePidFile_WhenPidFileCannotBeReplaced_ShouldThrowAndRemoveTemporaryFile()
	{
		// Arrange - a directory where the PID file belongs can never be replaced by a file
		string pidFilePath = SingleAppInstance.PidFilePath;
		Directory.CreateDirectory(pidFilePath);
		bool threw = false;

		// Act
		try
		{
			SingleAppInstance.WritePidFile();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			threw = true;
		}
		finally
		{
			Directory.Delete(pidFilePath, recursive: true);
		}

		// Assert
		Assert.IsTrue(threw, "WritePidFile should throw once every attempt to replace the PID file has failed");
		string[] temporaryFiles = Directory.GetFiles(SingleAppInstance.PidDirectoryPath, Path.GetFileName(pidFilePath) + ".*.tmp");
		Assert.IsEmpty(temporaryFiles, "The temporary file should be removed when the PID file cannot be replaced");
	}

	[TestMethod]
	public void TryWritePidFile_WhenPidFileCannotBeReplaced_ShouldThrowNamingThePidFile()
	{
		// Arrange - a directory where the PID file belongs is a lasting fault, not another instance
		string pidFilePath = SingleAppInstance.PidFilePath;
		Directory.CreateDirectory(pidFilePath);

		// Act
		Exception? failure = null;
		try
		{
			SingleAppInstance.TryWritePidFile();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			failure = ex;
		}
		finally
		{
			Directory.Delete(pidFilePath, recursive: true);
		}

		// Assert
		Assert.IsNotNull(failure, "A PID file that can never be written must not read as another instance claiming it");
		Assert.Contains(pidFilePath, failure.Message, "The failure should name the PID file");
	}

	[TestMethod]
	[DataRow("WINDOWS", unchecked((int)0x80070020), true, DisplayName = "Windows sharing violation")]
	[DataRow("WINDOWS", unchecked((int)0x80070021), true, DisplayName = "Windows lock violation")]
	[DataRow("WINDOWS", unchecked((int)0x80070005), false, DisplayName = "Windows access denied")]
	[DataRow("LINUX", 11, true, DisplayName = "Linux EWOULDBLOCK")]
	[DataRow("LINUX", 13, false, DisplayName = "Linux EACCES")]
	[DataRow("LINUX", 35, false, DisplayName = "Linux errno 35 is not EWOULDBLOCK")]
	[DataRow("OSX", 35, true, DisplayName = "macOS EWOULDBLOCK")]
	[DataRow("OSX", 11, false, DisplayName = "macOS errno 11 is not EWOULDBLOCK")]
	[DataRow("FREEBSD", 35, true, DisplayName = "FreeBSD EWOULDBLOCK")]
	[DataRow("FREEBSD", 13, false, DisplayName = "FreeBSD EACCES")]
	[DataRow(null, 5, true, DisplayName = "Unknown platform keeps every I/O failure as another instance")]
	public void IsLockViolation_RecognisesEachPlatformsLockError(string? platformName, int code, bool expected)
	{
		// Arrange
		OSPlatform? platform = platformName is null ? null : OSPlatform.Create(platformName);

		// Act
		bool result = SingleAppInstance.IsLockViolation(code, platform);

		// Assert
		Assert.AreEqual(expected, result);
	}

	[TestMethod]
	public void IsAlreadyRunning_WhenPidFileCannotBeRead_ShouldThrow()
	{
		// Arrange - reading a directory fails with access denied on every platform, and keeps failing
		string pidFilePath = SingleAppInstance.PidFilePath;
		Directory.CreateDirectory(pidFilePath);

		// Act
		Exception? failure = null;
		try
		{
			SingleAppInstance.IsAlreadyRunning();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			failure = ex;
		}
		finally
		{
			Directory.Delete(pidFilePath, recursive: true);
		}

		// Assert
		Assert.IsNotNull(failure, "A PID file that can never be read must not read as another instance running");
		Assert.Contains(pidFilePath, failure.Message, "The failure should name the PID file");
	}

	[TestMethod]
	public void ShouldLaunch_WhenPidFileCannotBeReadOrWritten_ShouldThrowInsteadOfReturningFalse()
	{
		// Arrange
		string pidFilePath = SingleAppInstance.PidFilePath;
		Directory.CreateDirectory(pidFilePath);

		// Act
		bool? result = null;
		bool threw = false;
		try
		{
			result = SingleAppInstance.ShouldLaunch();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			threw = true;
		}
		finally
		{
			Directory.Delete(pidFilePath, recursive: true);
		}

		// Assert
		Assert.IsTrue(threw, $"ShouldLaunch should report an unusable PID file, but returned {result}");
	}

	[TestMethod]
	public void ShouldLaunch_WhenPidFolderIsReadOnly_ShouldThrowInsteadOfReturningFalse()
	{
		// Arrange - a folder left owned by another user, as a run under sudo leaves it
		if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
		{
			Assert.Inconclusive("Folder permissions only deny an unprivileged process on Unix");
			return;
		}

		string pidDirectoryPath = SingleAppInstance.PidDirectoryPath;
		UnixFileMode originalMode = File.GetUnixFileMode(pidDirectoryPath);
		File.SetUnixFileMode(pidDirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);

		// Act
		bool? result = null;
		bool threw = false;
		try
		{
			result = SingleAppInstance.ShouldLaunch();
		}
		catch (UnauthorizedAccessException)
		{
			threw = true;
		}
		finally
		{
			File.SetUnixFileMode(pidDirectoryPath, originalMode);
		}

		// Assert
		Assert.IsTrue(threw, $"ShouldLaunch should report a PID folder it cannot write to, but returned {result}");
	}

	[TestMethod]
	public void TryWritePidFile_WhenPidFileCanBeWritten_ShouldReturnTrue()
	{
		// Act
		bool result = SingleAppInstance.TryWritePidFile();

		// Assert
		Assert.IsTrue(result);
		ProcessInfo? processInfo = JsonSerializer.Deserialize(File.ReadAllText(SingleAppInstance.PidFilePath), TestJsonContext.Default.ProcessInfo);
		Assert.IsNotNull(processInfo);
		Assert.AreEqual(Environment.ProcessId, processInfo.ProcessId);
	}

	[TestMethod]
	public void PidDirectoryPath_ShouldNotBeEmpty()
	{
		// Act
		string path = SingleAppInstance.PidDirectoryPath;

		// Assert
		Assert.IsFalse(string.IsNullOrEmpty(path), "PidDirectoryPath should not be null or empty");
	}

	[TestMethod]
	public void PidFilePath_ShouldContainSingleAppInstance()
	{
		// Act
		string path = SingleAppInstance.PidFilePath;

		// Assert
		Assert.IsFalse(string.IsNullOrEmpty(path), "PidFilePath should not be null or empty");
		Assert.IsTrue(path.Contains("SingleAppInstance", StringComparison.Ordinal), "PidFilePath should contain SingleAppInstance");
		Assert.IsTrue(path.EndsWith(".pid", StringComparison.Ordinal), "PidFilePath should end with .pid");
	}

	[TestMethod]
	public void IsAlreadyRunning_WithLegacyPidFile_NegativePid_ShouldReturnFalse()
	{
		// Arrange - legacy PID file with a negative number (not valid JSON, but valid int)
		string pidFilePath = SingleAppInstance.PidFilePath;
		File.WriteAllText(pidFilePath, "-12345");

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result, "Should return false for a negative PID in legacy format");
	}

	[TestMethod]
	public void WritePidFile_ContentShouldBeValidJson()
	{
		// Act
		SingleAppInstance.WritePidFile();

		// Assert
		string content = File.ReadAllText(SingleAppInstance.PidFilePath);
		ProcessInfo? deserialized = null;
		try
		{
			deserialized = JsonSerializer.Deserialize(content, TestJsonContext.Default.ProcessInfo);
		}
		catch (JsonException)
		{
			Assert.Fail("WritePidFile should produce valid JSON content");
		}

		Assert.IsNotNull(deserialized);
		Assert.IsTrue(deserialized.ProcessId > 0, "ProcessId should be positive");
		Assert.IsFalse(string.IsNullOrEmpty(deserialized.ProcessName), "ProcessName should not be empty");
	}

	[TestMethod]
	public void WritePidFile_CalledTwice_ShouldSucceed()
	{
		// Act - calling WritePidFile twice should not throw
		SingleAppInstance.WritePidFile();
		SingleAppInstance.WritePidFile();

		// Assert
		Assert.IsTrue(File.Exists(SingleAppInstance.PidFilePath));
		string content = File.ReadAllText(SingleAppInstance.PidFilePath);
		ProcessInfo? processInfo = JsonSerializer.Deserialize(content, TestJsonContext.Default.ProcessInfo);
		Assert.IsNotNull(processInfo);
		Assert.AreEqual(Environment.ProcessId, processInfo.ProcessId);
	}

	[TestMethod]
	public void IsAlreadyRunning_WithWhitespacePidFile_ShouldReturnFalse()
	{
		// Arrange
		string pidFilePath = SingleAppInstance.PidFilePath;
		File.WriteAllText(pidFilePath, "   ");

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result);
	}

	[TestMethod]
	public void IsAlreadyRunning_WithJsonArrayInPidFile_ShouldReturnFalse()
	{
		// Arrange - valid JSON but wrong type (array instead of object)
		string pidFilePath = SingleAppInstance.PidFilePath;
		File.WriteAllText(pidFilePath, "[1, 2, 3]");

		// Act
		bool result = SingleAppInstance.IsAlreadyRunning();

		// Assert
		Assert.IsFalse(result);
	}

	/// <summary>
	/// Describes a running process the way <see cref="SingleAppInstance.WritePidFile"/> records one.
	/// </summary>
	private static SingleAppInstance.ProcessInfo DescribeProcessForLibrary(Process process) => new()
	{
		ProcessId = process.Id,
		ProcessName = process.ProcessName,
		StartTime = process.StartTime,
		MainModuleFileName = process.MainModule?.FileName,
	};

	private static ProcessInfo DescribeProcess(Process process) => new()
	{
		ProcessId = process.Id,
		ProcessName = process.ProcessName,
		StartTime = process.StartTime,
		MainModuleFileName = process.MainModule?.FileName,
	};

	/// <summary>
	/// Describes a process as a Linux PID file does, reading its start tick and the boot ID from /proc
	/// independently of the library.
	/// </summary>
	private static ProcessInfo DescribeProcessWithLinuxIdentity(Process process)
	{
		ProcessInfo processInfo = DescribeProcess(process);
		string stat = File.ReadAllText($"/proc/{process.Id}/stat");
		string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
		processInfo.LinuxStartTicks = long.Parse(fields[19], System.Globalization.CultureInfo.InvariantCulture);
		processInfo.LinuxBootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
		return processInfo;
	}

	/// <summary>
	/// Finds a running process that is neither the current process nor shares its process name.
	/// </summary>
	private static Process? FindRunningProcessWithDifferentName(Process currentProcess)
	{
		foreach (Process candidate in Process.GetProcesses())
		{
			bool isUsable = false;

			try
			{
				// A name that prefixes the current process's name is excluded too, because a
				// platform that truncates process names reports this application that way
				isUsable = candidate.Id != currentProcess.Id &&
					!currentProcess.ProcessName.StartsWith(candidate.ProcessName, StringComparison.Ordinal);
			}
			catch (InvalidOperationException)
			{
				// Process exited between enumeration and inspection
			}
			catch (Win32Exception)
			{
				// Process details are not accessible
			}

			if (isUsable)
			{
				return candidate;
			}

			candidate.Dispose();
		}

		return null;
	}

	/// <summary>
	/// Launches a long-lived process that reports this application's own process name, so it is
	/// indistinguishable from another instance to a PID file that records nothing but a PID.
	/// </summary>
	private static HelperProcess StartInstanceLookalikeProcess()
	{
		using Process currentProcess = Process.GetCurrentProcess();
		string currentProcessName = currentProcess.ProcessName;

		HelperProcess helper = HelperProcess.Start(currentProcessName);
		string helperProcessName = helper.Process.ProcessName;

		// Some platforms report another process's name truncated, so a prefix of the current
		// process's name is as close a lookalike as the platform allows
		bool isLookalike = string.Equals(helperProcessName, currentProcessName, StringComparison.Ordinal) ||
			(helperProcessName.Length < currentProcessName.Length &&
				currentProcessName.StartsWith(helperProcessName, StringComparison.Ordinal));

		if (!isLookalike)
		{
			helper.Dispose();
			Assert.Inconclusive($"Could not launch a helper process named '{currentProcessName}' on this platform; it reported '{helperProcessName}'");
		}

		return helper;
	}

	/// <summary>
	/// A long-lived child process used to stand in for another instance of the application.
	/// </summary>
	/// <remarks>
	/// A long-running system executable is copied to a temporary file named after the requested
	/// process name, so the child reports that process name to the operating system.
	/// </remarks>
	private sealed class HelperProcess : IDisposable
	{
		private readonly string temporaryDirectory;

		public Process Process { get; }

		private HelperProcess(Process process, string temporaryDirectory)
		{
			Process = process;
			this.temporaryDirectory = temporaryDirectory;
		}

		public static HelperProcess Start(string processName)
		{
			bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
			string systemExecutable = isWindows
				? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe")
				: "/bin/sleep";
			string arguments = isWindows ? "-n 120 127.0.0.1" : "120";

			string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"{nameof(SingleAppInstanceTests)}_{Guid.NewGuid():N}");
			Directory.CreateDirectory(temporaryDirectory);

			string executable = Path.Combine(temporaryDirectory, isWindows ? $"{processName}.exe" : processName);
			File.Copy(systemExecutable, executable);

			if (!isWindows)
			{
				File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}

			ProcessStartInfo startInfo = new(executable, arguments)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			Process? process = Process.Start(startInfo);
			Assert.IsNotNull(process, "Should be able to start a helper process");

			HelperProcess helper = new(process, temporaryDirectory);
			helper.WaitForMainModule();
			return helper;
		}

		/// <summary>
		/// Waits until the helper's main module can be read, since Windows reports none until the
		/// loader has finished starting the process, and a test that records it too early would
		/// describe a different process from the one IsAlreadyRunning later inspects.
		/// </summary>
		private void WaitForMainModule()
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
			{
				Process.Refresh();
				try
				{
					if (Process.MainModule?.FileName is not null)
					{
						return;
					}
				}
				catch (Win32Exception)
				{
					// The module list is not readable yet
				}

				Thread.Sleep(50);
			}

			Assert.Inconclusive("The helper process's main module never became readable");
		}

		public void Dispose()
		{
			try
			{
				if (!Process.HasExited)
				{
					Process.Kill();
					Process.WaitForExit(5000);
				}
			}
			catch (InvalidOperationException)
			{
				// Process already exited
			}
			catch (Win32Exception)
			{
				// Process could not be signalled
			}

			Process.Dispose();

			try
			{
				Directory.Delete(temporaryDirectory, recursive: true);
			}
			catch (IOException)
			{
				// Temporary files are left for the operating system to reclaim
			}
			catch (UnauthorizedAccessException)
			{
				// Temporary files are left for the operating system to reclaim
			}
		}
	}

	// This class needs to mirror the internal ProcessInfo class for testing. It is read and written
	// through TestJsonContext because the test project, like a trimmed or Native AOT app, runs with
	// reflection-based serialization disabled.
	internal sealed class ProcessInfo
	{
		public int ProcessId { get; set; }
		public string? ProcessName { get; set; }
		public DateTime StartTime { get; set; }
		public string? MainModuleFileName { get; set; }
		public long? LinuxStartTicks { get; set; }
		public string? LinuxBootId { get; set; }
	}
}
