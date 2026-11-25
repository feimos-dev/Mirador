using System.Text.Json.Serialization;

namespace Mirador.Core.Session;

public sealed class SessionFile(string path)
{
    public void Write(string profileId, string? moduleId, IReadOnlyDictionary<string, string> data) =>
        JsonFile.Save(path, new SavedSession(profileId, moduleId, new Dictionary<string, string>(data)), SessionJsonContext.Default.SavedSession);

    public (string ProfileId, string? ModuleId, IReadOnlyDictionary<string, string> Data)? Read()
    {
        var saved = JsonFile.Load(path, SessionJsonContext.Default.SavedSession, Usable);
        return saved is null ? null : (saved.ProfileId, saved.ModuleId, saved.Data!);
    }

    public void Delete()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static SavedSession? Usable(SavedSession saved) =>
        string.IsNullOrEmpty(saved.ProfileId)
            ? null
            : saved with
            {
                Data = (saved.Data ?? []).Where(e => e.Value is not null).ToDictionary(e => e.Key, e => e.Value),
            };
}

internal sealed record SavedSession(string ProfileId, string? ModuleId, Dictionary<string, string>? Data);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(SavedSession))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;
