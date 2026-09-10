using System.Text.Json.Serialization;

namespace ComfyTray;

/// <summary>
/// Source-generated serialisation for the guard protocol.
///
/// <para>
/// Generated rather than reflection-based for two reasons: it keeps the trim and AOT analyzers
/// quiet under the repository's <c>AnalysisLevel=latest-All</c> and
/// <c>TreatWarningsAsErrors</c>, and it means a malformed message is a deserialisation failure
/// rather than a reflective surprise on a service running as LocalSystem.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuardRequest))]
[JsonSerializable(typeof(GuardResponse))]
internal sealed partial class GuardJsonContext : JsonSerializerContext;
