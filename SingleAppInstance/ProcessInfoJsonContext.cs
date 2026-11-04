// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SingleAppInstance;

using System.Text.Json.Serialization;

/// <summary>
/// Source-generated serialization for the PID file record.
/// </summary>
/// <remarks>
/// An app published trimmed or as Native AOT runs with reflection-based System.Text.Json disabled,
/// so the reflection overloads would throw on its first launch. The JSON shape is the same as the
/// reflection serializer's, so PID files written by earlier versions still read.
/// </remarks>
[JsonSerializable(typeof(SingleAppInstance.ProcessInfo))]
internal sealed partial class ProcessInfoJsonContext : JsonSerializerContext;
