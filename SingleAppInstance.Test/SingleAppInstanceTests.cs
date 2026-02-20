// Copyright (c) ktsu.dev
// All rights reserved.
// Licensed under the MIT license.

namespace ktsu.SingleAppInstance.Test;

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

[TestClass]
[DoNotParallelize]
public class SingleAppInstanceTests
{
	[TestInitialize]
	public void TestInitialize()
	{
		// Ensure the PID directory exists and the file is deleted before each test
		string pidFilePath = SingleAppInstance.PidFilePath;
		Directory.CreateDirectory(SingleAppInstance.PidDirectoryPath);
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
		ProcessInfo? processInfo = JsonSerializer.Deserialize<ProcessInfo>(fileContent);

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
		ProcessInfo? processInfo = JsonSerializer.Deserialize<ProcessInfo>(content);
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

		string json = JsonSerializer.Serialize(processInfo);
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
	public void IsAlreadyRunning_WithLegacyPidFile_RunningProcess_ShouldReturnTrue()
	{
		// Arrange - legacy PID file with a PID of a process that IS running
		// Use a well-known process that should always be running
		string pidFilePath = SingleAppInstance.PidFilePath;
		using Process currentProcess = Process.GetCurrentProcess();
		Process? targetProcess = null;

		try
		{
			// Find a different running process to use
			foreach (Process p in Process.GetProcesses())
			{
				if (p.Id != currentProcess.Id)
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
			File.WriteAllText(pidFilePath, targetProcess.Id.ToString(CultureInfo.InvariantCulture));

			// Act
			bool result = SingleAppInstance.IsAlreadyRunning();

			// Assert
			Assert.IsTrue(result, "Should return true for a running process in legacy format");
		}
		finally
		{
			targetProcess?.Dispose();
		}
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

		string json = JsonSerializer.Serialize(processInfo);
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

			string json = JsonSerializer.Serialize(processInfo);
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

		string json = JsonSerializer.Serialize(processInfo);
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

		string json = JsonSerializer.Serialize(processInfo);
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
		ProcessInfo? processInfo = JsonSerializer.Deserialize<ProcessInfo>(fileContent);
		Assert.IsNotNull(processInfo);
		Assert.AreEqual(Environment.ProcessId, processInfo.ProcessId);
	}

	[TestMethod]
	public void ShouldLaunch_WhenAlreadyRunning_ShouldReturnFalse()
	{
		// Arrange - Write a PID file for a different running process using legacy format
		// This ensures IsAlreadyRunning() returns true on the first call
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

			// Write legacy format PID file so IsAlreadyRunning returns true
			File.WriteAllText(pidFilePath, targetProcess.Id.ToString(CultureInfo.InvariantCulture));

			// Act
			bool result = SingleAppInstance.ShouldLaunch();

			// Assert
			Assert.IsFalse(result, "ShouldLaunch should return false when another instance is detected");
		}
		finally
		{
			targetProcess?.Dispose();
		}
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
			deserialized = JsonSerializer.Deserialize<ProcessInfo>(content);
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
		ProcessInfo? processInfo = JsonSerializer.Deserialize<ProcessInfo>(content);
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

	// This class needs to mirror the internal ProcessInfo class for testing
	private sealed class ProcessInfo
	{
		public int ProcessId { get; set; }
		public string? ProcessName { get; set; }
		public DateTime StartTime { get; set; }
		public string? MainModuleFileName { get; set; }
	}
}
