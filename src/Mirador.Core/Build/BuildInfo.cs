using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mirador.Core.Build;

public sealed record BuildInfo(
    string? SourceUrl,
    string? Commit,
    string? Describe,
    string? Compiler,
    string? MsvcVersion,
    string? VcpkgCommit,
    IReadOnlyList<string>? Options,
    string? BuiltAt)
{
    public const string FileName = "build-info.json";

    public static BuildInfo? Load(string installDir)
    {
        var path = Path.Combine(installDir, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), BuildInfoJsonContext.Default.BuildInfo) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    [JsonIgnore]
    public string ShortCommit => Commit is { Length: > 10 } c ? c[..10] : Commit ?? "unknown";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BuildInfo))]
internal sealed partial class BuildInfoJsonContext : JsonSerializerContext;
