// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SingleAppInstance.Test;

using System.Text.Json.Serialization;

/// <summary>
/// Source-generated serialization for the test's mirror of the PID file record.
/// </summary>
/// <remarks>
/// The test project disables reflection-based serialization, as a trimmed or Native AOT app does,
/// so that every test exercises the library the way those apps run it.
/// </remarks>
[JsonSerializable(typeof(SingleAppInstanceTests.ProcessInfo))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
