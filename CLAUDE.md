# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

`ktsu.SingleAppInstance` is a .NET library that ensures only one instance of an application is running at a time. It uses a PID file (JSON-serialized `ProcessInfo`) stored in the app data directory, with a 1-second race condition window for simultaneous startup detection.

## Build Commands

```bash
# Restore, build, and test (standard workflow)
dotnet restore
dotnet build
dotnet test

# Run a single test
dotnet test --filter "FullyQualifiedName~SingleAppInstanceTests.IsAlreadyRunning_WhenPidFileDoesNotExist_ShouldReturnFalse"

# Build specific configuration
dotnet build -c Release
```

Note: The `ShouldLaunch` test takes ~1 second due to `Thread.Sleep` race condition handling.

## Project Structure

This is a .NET library (`ktsu.SingleAppInstance`) that enforces single-instance execution of applications. The solution uses:

- **ktsu.Sdk** - Custom SDK providing shared build configuration
- **MSTest.Sdk** - Test project SDK with Microsoft Testing Platform
- Multi-targeting: net10.0, net9.0, net8.0, net7.0, net6.0, net5.0, netstandard2.0, netstandard2.1

### Key Files

- [SingleAppInstance/SingleAppInstance.cs](SingleAppInstance/SingleAppInstance.cs) - Static class with all logic: `ExitIfAlreadyRunning()`, `ShouldLaunch()`, `IsAlreadyRunning()`, `WritePidFile()`, and the `ProcessInfo` model
- [SingleAppInstance/AssemblyInfo.cs](SingleAppInstance/AssemblyInfo.cs) - `InternalsVisibleTo` for test project access to internal methods

### Dependencies

- `ktsu.AppDataStorage` - Provides `AppData.Path` for PID file location
- `ktsu.Semantics.Paths` - Type-safe path handling (`AbsoluteDirectoryPath`, `AbsoluteFilePath`, `FileName`)
- `Polyfill` - Backfill APIs for older target frameworks

## Architecture

Single static class (`SingleAppInstance`) in `ktsu.SingleAppInstance` namespace with two public methods:

- `ExitIfAlreadyRunning()` - calls `Environment.Exit(0)` if another instance is detected
- `ShouldLaunch()` - returns bool; writes PID file, sleeps 1s for race detection, re-checks

Internal methods `IsAlreadyRunning()` and `WritePidFile()` are exposed to tests via `InternalsVisibleTo`.

### PID File Format

JSON-serialized `ProcessInfo` with fields: `ProcessId`, `ProcessName`, `StartTime`, `MainModuleFileName`, and on Linux `LinuxStartTicks` (field 22 of `/proc/<pid>/stat`) and `LinuxBootId`. On Linux, .NET derives another process's `StartTime` from the current wall clock, so it moves when the clock is stepped. When both the PID file and the running process have the Linux identity, it is compared instead of `StartTime`. Includes backward compatibility for legacy files that stored only a plain integer PID.

Serialization goes through the source-generated `ProcessInfoJsonContext`, never the reflection overloads: apps published trimmed or Native AOT run with reflection-based System.Text.Json disabled. The library sets `IsAotCompatible` on net7.0+, so a reflection call is a build error (IL2026/IL3050). The test project sets `JsonSerializerIsReflectionEnabledByDefault=false` and reads the PID file through its own `TestJsonContext`, so every test runs the way those apps do.

### Multi-targeting

The library targets multiple frameworks via `ktsu.Sdk`. Uses `#if NET5_0_OR_GREATER` preprocessor directives (e.g., `Environment.ProcessId` vs `Process.GetCurrentProcess().Id`).

## Testing

Tests use MSTest (`MSTest.Sdk`) targeting net10.0 only. Tests are marked `[DoNotParallelize]` because they share the PID file on disk. Each test cleans up the PID file in `[TestInitialize]`. The test project mirrors the internal `ProcessInfo` class as a nested class, serialized through `TestJsonContext`.

## CI/CD

Uses GitHub Actions workflow (`dotnet.yml`) for CI pipeline. Version increments are controlled by commit message tags: `[major]`, `[minor]`, `[patch]`, `[pre]`. Auto-generated files (`VERSION.md`, `CHANGELOG.md`, `LICENSE.md`) should not be edited manually.

## Code Quality

Do not add global suppressions for warnings. Use explicit suppression attributes with justifications when needed, with preprocessor defines only as fallback. Make the smallest, most targeted suppressions possible.
