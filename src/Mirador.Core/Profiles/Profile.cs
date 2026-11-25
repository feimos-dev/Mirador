using System.Text.Json.Serialization;

namespace Mirador.Core.Profiles;

public enum SourceKind
{
    Folder,

    Mirador,

    Branch,
}

public sealed record BuildSource(
    SourceKind Kind,
    string? Folder = null,
    string? Url = null,
    string? Ref = null,
    IReadOnlyList<string>? CMakeOptions = null)
{
    public const string UpstreamUrl = "https://gitlab.freedesktop.org/monado/monado.git";

    public const string UpstreamRef = "main";

    public static BuildSource Mirador { get; } = new(SourceKind.Mirador);

    public static BuildSource UpstreamMain { get; } = new(SourceKind.Branch, Url: UpstreamUrl, Ref: UpstreamRef);

    public static BuildSource FromBranch(string url, string gitRef, IReadOnlyList<string>? options = null) =>
        new(SourceKind.Branch, Url: url, Ref: gitRef, CMakeOptions: options is { Count: > 0 } ? [.. options] : null);

    [JsonIgnore]
    public IReadOnlyList<string> Options => CMakeOptions ?? [];

    [JsonIgnore]
    public bool IsUpstreamMain =>
        Kind == SourceKind.Branch && Url == UpstreamUrl && Ref == UpstreamRef && Options.Count == 0;

    public string Describe() => Kind switch
    {
        SourceKind.Mirador => "the bundled Monado",
        SourceKind.Folder => $"build folder {Folder}",
        SourceKind.Branch when IsUpstreamMain => "upstream main, built here",
        SourceKind.Branch => $"{Url} {Ref}, built here",
        _ => Kind.ToString(),
    };

    public bool Equals(BuildSource? other) =>
        other is not null && Kind == other.Kind && Folder == other.Folder && Url == other.Url && Ref == other.Ref
        && Options.SequenceEqual(other.Options);

    public override int GetHashCode() => HashCode.Combine(Kind, Folder, Url, Ref, Options.Count);
}

public sealed record EnvVar(string Name, string Value);

public sealed record Profile(
    string Id,
    string Name,
    string? ModuleId,
    BuildSource Monado,
    IReadOnlyList<EnvVar> ExtraEnvironment,
    IReadOnlyDictionary<string, string> ModuleOptions,
    bool? Lighthouse = null,
    bool RunAsAdministrator = false,
    DirectModeKind? DirectMode = null)
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    public bool UsesBaseStations(bool moduleUsesSteamVrLighthouse) =>
        Lighthouse ?? Mirador.Core.Lighthouse.LighthouseConflicts.UsesLighthouse(this, moduleUsesSteamVrLighthouse);

    public DirectModeKind DirectModeWith(Modules.IHeadsetModule? module) =>
        DirectMode ?? module?.DefaultDirectMode ?? DirectModeKind.None;
}
