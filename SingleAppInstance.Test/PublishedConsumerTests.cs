// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SingleAppInstance.Test;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using ktsu.AppDataStorage;

/// <summary>
/// Publishes a tiny consumer of the library trimmed, and Native AOT, and runs two instances of it.
/// The library sets <c>IsAotCompatible</c>, so those apps are supported, yet only a published app
/// exercises the trimmer and the AOT compiler: a JIT test host never fails the way they do (#221).
/// </summary>
[TestClass]
[DoNotParallelize]
public class PublishedConsumerTests
{
	private const string ConsumerProgram = """
		bool launched = ktsu.SingleAppInstance.SingleAppInstance.ShouldLaunch();
		System.Console.WriteLine("ShouldLaunch=" + launched);
		System.Console.Out.Flush();
		if (launched && args.Length > 0 && args[0] == "hold")
		{
			System.Console.ReadLine();
		}
		""";

	public TestContext TestContext { get; set; } = null!;

	[TestMethod]
	public void PidFilePath_ShouldBeTheFileAppDataStorageResolved()
	{
		// The path is built from BCL strings now; existing installs must keep their PID file.
		string appDataStoragePath = Path.Combine(AppData.Path.ToString(), $".{nameof(SingleAppInstance)}.pid");

		Assert.AreEqual(AppData.Path.ToString(), SingleAppInstance.PidDirectoryPath);
		Assert.AreEqual(appDataStoragePath, SingleAppInstance.PidFilePath);
	}

	[TestMethod]
	[Timeout(600_000, CooperativeCancellation = true)]
	public void TrimmedConsumer_ShouldLaunchOnceAndRefuseASecondInstance() =>
		PublishAndRunTwice("<SelfContained>true</SelfContained><PublishTrimmed>true</PublishTrimmed>");

	[TestMethod]
	[Timeout(600_000, CooperativeCancellation = true)]
	public void NativeAotConsumer_ShouldLaunchOnceAndRefuseASecondInstance() =>
		PublishAndRunTwice("<PublishAot>true</PublishAot>");

	private void PublishAndRunTwice(string publishProperties)
	{
		// A name of its own gives the consumer its own app-data folder, apart from the test host's.
		string consumerName = $"SaiConsumer{Guid.NewGuid():N}"[..24];
		string workDirectory = Path.Combine(Path.GetTempPath(), consumerName);
		string appDataDirectory = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
			consumerName);
		Directory.CreateDirectory(workDirectory);

		try
		{
			// The publish properties go in the project file, not on the command line: a global
			// RuntimeIdentifier or PublishTrimmed would flow into the library's netstandard2.0 build.
			string project = $"""
				<Project Sdk="Microsoft.NET.Sdk">
				  <PropertyGroup>
				    <OutputType>Exe</OutputType>
				    <TargetFramework>net10.0</TargetFramework>
				    <AssemblyName>{consumerName}</AssemblyName>
				    <RuntimeIdentifier>{RuntimeInformation.RuntimeIdentifier}</RuntimeIdentifier>
				    {publishProperties}
				  </PropertyGroup>
				  <ItemGroup>
				    <ProjectReference Include="{LibraryProjectPath()}" SetTargetFramework="TargetFramework=net10.0" />
				  </ItemGroup>
				</Project>
				""";
			File.WriteAllText(Path.Combine(workDirectory, $"{consumerName}.csproj"), project);
			File.WriteAllText(Path.Combine(workDirectory, "Program.cs"), ConsumerProgram);

			string publishDirectory = Path.Combine(workDirectory, "publish");
			(int publishExitCode, string publishOutput) = Run("dotnet", $"publish -c Release -o \"{publishDirectory}\"", workDirectory, TimeSpan.FromMinutes(9));
			Assert.AreEqual(0, publishExitCode, $"Publishing the consumer failed:{Environment.NewLine}{publishOutput}");

			string executable = Path.Combine(publishDirectory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{consumerName}.exe" : consumerName);

			using Process first = StartConsumer(executable, "hold");
			string? firstLine = ReadLineWithin(first, TimeSpan.FromSeconds(60));
			Assert.AreEqual("ShouldLaunch=True", firstLine, $"The first instance should launch. Its error output:{Environment.NewLine}{ReadErrors(first)}");

			(int secondExitCode, string secondOutput) = Run(executable, string.Empty, workDirectory, TimeSpan.FromSeconds(60));
			Assert.AreEqual(0, secondExitCode, $"The second instance failed:{Environment.NewLine}{secondOutput}");
			StringAssert.Contains(secondOutput, "ShouldLaunch=False", "The second instance should see the first one running");

			first.StandardInput.WriteLine();
			Assert.IsTrue(first.WaitForExit(30_000), "The first instance should exit once released");
			Assert.AreEqual(0, first.ExitCode);
		}
		finally
		{
			TryDelete(workDirectory);
			TryDelete(appDataDirectory);
		}
	}

	private static string LibraryProjectPath([CallerFilePath] string testSourcePath = "") =>
		Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testSourcePath)!, "..", "SingleAppInstance", "SingleAppInstance.csproj"));

	private static Process StartConsumer(string executable, string arguments)
	{
		ProcessStartInfo startInfo = new(executable, arguments)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		Process? process = Process.Start(startInfo);
		Assert.IsNotNull(process, "Should be able to start the published consumer");
		return process;
	}

	private static string? ReadLineWithin(Process process, TimeSpan timeout)
	{
		Task<string?> line = process.StandardOutput.ReadLineAsync();
		return line.Wait(timeout) ? line.Result : null;
	}

	private static string ReadErrors(Process process)
	{
		if (!process.HasExited)
		{
			process.Kill();
		}

		process.WaitForExit(5000);
		return process.StandardError.ReadToEnd();
	}

	private (int ExitCode, string Output) Run(string fileName, string arguments, string workingDirectory, TimeSpan timeout)
	{
		ProcessStartInfo startInfo = new(fileName, arguments)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		using Process? process = Process.Start(startInfo);
		Assert.IsNotNull(process, $"Should be able to start {fileName}");

		StringBuilder output = new();
		object outputLock = new();
		void Append(object sender, DataReceivedEventArgs e)
		{
			lock (outputLock)
			{
				output.AppendLine(e.Data);
			}
		}

		process.OutputDataReceived += Append;
		process.ErrorDataReceived += Append;
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		if (!process.WaitForExit((int)timeout.TotalMilliseconds))
		{
			process.Kill(entireProcessTree: true);
			Assert.Fail($"{fileName} {arguments} did not finish within {timeout}");
		}

		process.WaitForExit();
		TestContext.CancellationTokenSource.Token.ThrowIfCancellationRequested();

		lock (outputLock)
		{
			return (process.ExitCode, output.ToString());
		}
	}

	private static void TryDelete(string directory)
	{
		try
		{
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
		catch (IOException)
		{
			// Best effort: a temporary folder left behind does not change the result
		}
		catch (UnauthorizedAccessException)
		{
			// Best effort, as above
		}
	}
}
