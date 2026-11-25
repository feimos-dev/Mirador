using System.Text.Json.Serialization;

namespace Mirador.Ipc;

[JsonConverter(typeof(HelperCommandConverter))]
public enum HelperCommand
{
    Ping,

    SetRuntimeBundled,

    StopPimax,

    GetPimaxEyeBlob,

    RevokeElevatedMonado,

    StartMonadoElevated,

    StopMonadoElevated,

    WaitMonadoElevated,

    GetInstallFolder,

    InstallMonado,

    RollBackMonado,

    SetRuntimeApproved,

    ListApprovedBuilds,

    ForgetApprovedBuild,

    RegisterLayer,

    UnregisterLayer,
}

public sealed record ElevatedStart(
    [property: JsonRequired] int LighthouseWaitMs,
    [property: JsonRequired] int RenderScalePercent,
    [property: JsonRequired] int CompositorMinTimeMs,
    [property: JsonRequired] string Module,
    string Build = "",
    bool Headless = false,
    bool ElevationFix = false,
    bool StandbyOnExit = false,
    string ProfileId = "",
    IReadOnlyList<KeyValuePair<string, string>>? Variables = null,
    string DirectMode = "");

public sealed record HelperRequest(
    [property: JsonRequired] HelperCommand Command, ElevatedStart? Start = null, string? Version = null, string? Build = null, bool? Clean = null,
    string? Layer = null);

[JsonConverter(typeof(HelperRefusalConverter))]
public enum HelperRefusal
{
    SteamVrChanged,
}

public sealed record HelperReply(
    [property: JsonRequired] bool Ok,
    string? Error = null,
    string? Value = null,
    HelperRefusal? Refusal = null)
{
    [JsonIgnore]
    public bool NotDelivered { get; init; }
}

internal sealed class HelperCommandConverter : JsonStringEnumConverter<HelperCommand>
{
    public HelperCommandConverter() : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}

internal sealed class HelperRefusalConverter : JsonStringEnumConverter<HelperRefusal>
{
    public HelperRefusalConverter() : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelperRequest))]
[JsonSerializable(typeof(HelperReply))]
internal sealed partial class HelperJsonContext : JsonSerializerContext
{
}
