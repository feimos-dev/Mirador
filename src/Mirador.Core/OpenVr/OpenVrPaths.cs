using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mirador.Core.OpenVr;

public enum OpenVrTarget
{
    None,

    Monado,

    SteamVr,

    Other,
}

public sealed record OpenVrState(OpenVrTarget Target, string? First, string? Problem);

public sealed class OpenVrPaths(string file, string ourRuntimeDir)
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentCharacter = '\t',
        IndentSize = 1,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");

    public OpenVrState Read()
    {
        var (doc, problem) = Load();
        if (problem is not null)
        {
            return new OpenVrState(OpenVrTarget.None, null, problem);
        }

        var first = doc is null ? null : RuntimeList(doc).FirstOrDefault();
        return first is null ? new OpenVrState(OpenVrTarget.None, null, null) : new OpenVrState(Classify(first), first, null);
    }

    public string? UseMonado()
    {
        var (doc, problem) = Load();
        if (problem is not null)
        {
            return problem;
        }

        doc ??= new JsonObject { ["jsonid"] = "vrpathreg", ["runtime"] = new JsonArray(), ["version"] = 1 };
        var others = RuntimeList(doc).Where(entry => !SamePath(entry, ourRuntimeDir));
        return Save(doc, [Path.TrimEndingDirectorySeparator(Full(ourRuntimeDir)), .. others]);
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Full(a)),
            Path.TrimEndingDirectorySeparator(Full(b)),
            StringComparison.OrdinalIgnoreCase);

    private static string Full(string path)
    {
        var trimmed = path.Trim().Replace('/', '\\');
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }

    private OpenVrTarget Classify(string entry)
    {
        if (SamePath(entry, ourRuntimeDir))
        {
            return OpenVrTarget.Monado;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Full(entry)));
        return string.Equals(name, "SteamVR", StringComparison.OrdinalIgnoreCase) ? OpenVrTarget.SteamVr : OpenVrTarget.Other;
    }

    private static List<string> RuntimeList(JsonObject doc) =>
        doc["runtime"] is JsonArray list ? list.Select(n => n!.GetValue<string>()).ToList() : [];

    private (JsonObject? Doc, string? Problem) Load()
    {
        string text;
        try
        {
            if (!File.Exists(file))
            {
                return (null, null);
            }

            text = File.ReadAllText(file, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, Problem(e.Message.Trim().TrimEnd('.')));
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(
                text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            return (null, Problem("it isn't valid JSON"));
        }

        if (node is not JsonObject doc)
        {
            return (null, Problem("it isn't a JSON object"));
        }

        if (doc["runtime"] is { } runtime
            && (runtime is not JsonArray list || list.Any(entry => entry?.GetValueKind() != JsonValueKind.String)))
        {
            return (null, Problem("its \"runtime\" isn't a list of folders"));
        }

        return (doc, null);
    }

    private string Problem(string why) =>
        $"Mirador couldn't read the OpenVR paths file {file} ({why}), so it left it as it is. Fix or delete that file, then try again.";

    private string? Save(JsonObject doc, IEnumerable<string> runtimes)
    {
        doc["runtime"] = new JsonArray(runtimes.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        var temp = file + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream, WriterOptions))
                {
                    doc.WriteTo(writer);
                }

                stream.Write("\n"u8);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, file, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return $"Mirador couldn't write the OpenVR paths file {file} ({e.Message.Trim().TrimEnd('.')}); it is unchanged.";
        }
    }
}
