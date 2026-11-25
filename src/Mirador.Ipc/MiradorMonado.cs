using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mirador.Ipc;

public sealed record MiradorMonadoVersions(string? Current, string? Previous);

public static partial class MiradorMonado
{
    public const string CurrentFile = "current.json";

    public const string TagPrefix = "monado-";

    public static bool IsVersion(string? version) => version is not null && VersionPattern().IsMatch(version);

    public static DateOnly? DateOf(string? version) =>
        IsVersion(version)
        && DateOnly.TryParseExact(version!.Substring(TagPrefix.Length, 10), "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    public static MiradorMonadoVersions Read(string root)
    {
        var path = Path.Combine(root, CurrentFile);
        try
        {
            if (!File.Exists(path))
            {
                return new MiradorMonadoVersions(null, null);
            }

            var versions = JsonSerializer.Deserialize(File.ReadAllText(path), MiradorMonadoJsonContext.Default.MiradorMonadoVersions);
            return new MiradorMonadoVersions(
                IsVersion(versions?.Current) ? versions!.Current : null,
                IsVersion(versions?.Previous) ? versions!.Previous : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new MiradorMonadoVersions(null, null);
        }
    }

    public static void Write(string root, MiradorMonadoVersions versions)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, CurrentFile);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(versions, MiradorMonadoJsonContext.Default.MiradorMonadoVersions));
        File.Move(temp, path, overwrite: true);
    }

    public static string ManifestOf(string root, string version) => Path.Combine(FolderOf(root, version), "openxr_monado.json");

    public static bool IsManifestOf(string root, string manifest)
    {
        try
        {
            var full = Path.GetFullPath(manifest);
            var folder = Path.GetDirectoryName(full);
            return string.Equals(Path.GetFileName(full), "openxr_monado.json", StringComparison.OrdinalIgnoreCase)
                && folder is not null
                && IsVersion(Path.GetFileName(folder))
                && string.Equals(Path.GetDirectoryName(folder), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static string FolderOf(string root, string version) =>
        IsVersion(version) ? Path.Combine(root, version) : throw new ArgumentException($"Not a version of the bundled Monado: {version}", nameof(version));

    [GeneratedRegex(@"^monado-\d{4}\.\d{2}\.\d{2}-[0-9a-f]{7,40}\z")]
    private static partial Regex VersionPattern();
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(MiradorMonadoVersions))]
internal sealed partial class MiradorMonadoJsonContext : JsonSerializerContext;
