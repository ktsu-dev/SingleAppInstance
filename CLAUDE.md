# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

`ktsu.SingleAppInstance` is a .NET library that ensures only one instance of an application is running at a time. It uses a PID file (JSON-serialized `ProcessInfo`) stored in the app data directory, with a 1-second race condition window for simultaneous startup detection.

## Build and Test Commands

```bash
# Build
dotnet build

# Run all tests (note: ShouldLaunch test takes ~1 second due to Thread.Sleep race condition handling)
dotnet test

# Run a single test
dotnet test --filter "FullyQualifiedName~SingleAppInstanceTests.IsAlreadyRunning_WhenPidFileDoesNotExist_ShouldReturnFalse"
```

## Architecture

Single static class (`SingleAppInstance`) in [SingleAppInstance/SingleAppInstance.cs](SingleAppInstance/SingleAppInstance.cs) with two public methods:
- `ExitIfAlreadyRunning()` - calls `Environment.Exit(0)` if another instance is detected
- `ShouldLaunch()` - returns bool; writes PID file, sleeps 1s for race detection, re-checks

Internal methods `IsAlreadyRunning()` and `WritePidFile()` are exposed to tests via `InternalsVisibleTo` in [SingleAppInstance/AssemblyInfo.cs](SingleAppInstance/AssemblyInfo.cs).

### PID File Format

JSON-serialized `ProcessInfo` with fields: `ProcessId`, `ProcessName`, `StartTime`, `MainModuleFileName`. Includes backward compatibility for legacy files that stored only a plain integer PID.

### Key Dependencies

- `ktsu.AppDataStorage` - provides `AppData.Path` for PID file location
- `ktsu.Semantics.Paths` - type-safe path handling (`AbsoluteDirectoryPath`, `AbsoluteFilePath`, `FileName`)
- `Polyfill` - backfill APIs for older target frameworks

### Multi-targeting

The library targets multiple frameworks via `ktsu.Sdk`. Uses `#if NET5_0_OR_GREATER` preprocessor directives (e.g., `Environment.ProcessId` vs `Process.GetCurrentProcess().Id`).

## Testing

Tests use MSTest (`MSTest.Sdk`) targeting net10.0 only. Tests are marked `[DoNotParallelize]` because they share the PID file on disk. Each test cleans up the PID file in `[TestInitialize]`. The test project mirrors the internal `ProcessInfo` class as a private nested class for deserialization.

## Build System

Uses `ktsu.Sdk` custom MSBuild SDK with Central Package Management (`Directory.Packages.props`). Auto-generated files (`VERSION.md`, `CHANGELOG.md`, `LICENSE.md`) should not be edited manually.
