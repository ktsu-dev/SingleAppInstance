# ktsu.SingleAppInstance

> A .NET library that ensures only one instance of your application is running at a time.

[![License](https://img.shields.io/github/license/ktsu-dev/SingleAppInstance.svg?label=License&logo=nuget)](LICENSE.md)
[![NuGet Version](https://img.shields.io/nuget/v/ktsu.SingleAppInstance?label=Stable&logo=nuget)](https://nuget.org/packages/ktsu.SingleAppInstance)
[![NuGet Version](https://img.shields.io/nuget/vpre/ktsu.SingleAppInstance?label=Latest&logo=nuget)](https://nuget.org/packages/ktsu.SingleAppInstance)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ktsu.SingleAppInstance?label=Downloads&logo=nuget)](https://nuget.org/packages/ktsu.SingleAppInstance)
[![GitHub commit activity](https://img.shields.io/github/commit-activity/m/ktsu-dev/SingleAppInstance?label=Commits&logo=github)](https://github.com/ktsu-dev/SingleAppInstance/commits/main)
[![GitHub contributors](https://img.shields.io/github/contributors/ktsu-dev/SingleAppInstance?label=Contributors&logo=github)](https://github.com/ktsu-dev/SingleAppInstance/graphs/contributors)
[![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/ktsu-dev/SingleAppInstance/dotnet.yml?label=Build&logo=github)](https://github.com/ktsu-dev/SingleAppInstance/actions)

## Introduction

`ktsu.SingleAppInstance` is a lightweight .NET library that provides a robust mechanism to ensure only one instance of an application is running at a time. It uses a JSON-serialized PID file with multi-attribute process verification to accurately detect running instances, making it ideal for desktop applications, services, or any software that requires instance exclusivity to prevent resource conflicts or maintain data integrity.

## Features

- **Single Instance Enforcement**: Prevents multiple copies of your application from running simultaneously
- **Enhanced Process Identification**: Verifies running instances using multiple attributes (PID, process name, start time, executable path) for accurate detection
- **Race Condition Handling**: Includes a built-in 1-second delay to safely detect simultaneous startup attempts
- **PID File Management**: Stores process information as JSON in the application data directory
- **Backward Compatibility**: Gracefully handles legacy PID files that stored only a plain integer PID
- **Simple API**: Two methods — `ExitIfAlreadyRunning()` for automatic exit and `ShouldLaunch()` for custom logic
- **Multi-Target Support**: Works across .NET 10.0 through .NET 5.0, .NET Standard 2.0/2.1

## Installation

### Package Manager Console

```powershell
Install-Package ktsu.SingleAppInstance
```

### .NET CLI

```bash
dotnet add package ktsu.SingleAppInstance
```

### Package Reference

```xml
<PackageReference Include="ktsu.SingleAppInstance" Version="x.y.z" />
```

## Usage Examples

### Basic Example

The simplest way to use SingleAppInstance is to call `ExitIfAlreadyRunning` at the start of your application. If another instance is detected, the process exits automatically:

```csharp
using ktsu.SingleAppInstance;

class Program
{
    static void Main(string[] args)
    {
        SingleAppInstance.ExitIfAlreadyRunning();

        // Your application code here
        Console.WriteLine("Application is running.");
    }
}
```

### Custom Launch Logic

If you prefer to handle the duplicate-instance case yourself, use `ShouldLaunch()` which returns a boolean:

```csharp
using ktsu.SingleAppInstance;

class Program
{
    static void Main(string[] args)
    {
        if (SingleAppInstance.ShouldLaunch())
        {
            // Your application code here
            Console.WriteLine("Application is running.");
        }
        else
        {
            Console.WriteLine("Another instance is already running.");
        }
    }
}
```

### WPF Application Integration

```csharp
using System.Windows;
using ktsu.SingleAppInstance;

namespace MyWpfApp
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (!SingleAppInstance.ShouldLaunch())
            {
                MessageBox.Show("Application is already running.");
                Shutdown();
                return;
            }

            MainWindow = new MainWindow();
            MainWindow.Show();
        }
    }
}
```

## API Reference

### `SingleAppInstance`

A static class that provides single-instance enforcement for applications. Uses a PID file stored in the application data directory to track running instances.

#### Methods

| Name | Return Type | Description |
| --- | --- | --- |
| `ExitIfAlreadyRunning()` | `void` | Checks if another instance is running and calls `Environment.Exit(0)` if so |
| `ShouldLaunch()` | `bool` | Writes a PID file, waits 1 second for race condition detection, and returns `true` if safe to proceed |

## Contributing

Contributions are welcome! Feel free to open issues or submit pull requests.

## License

This project is licensed under the MIT License. See the [LICENSE.md](LICENSE.md) file for details.
