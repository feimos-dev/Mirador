using System.Text.Json.Serialization;

namespace Mirador.Core.Monado;

public sealed class MonadoSettingsStore(string path)
{
    public MonadoSettings Load() =>
        JsonFile.Load(path, MonadoSettingsJsonContext.Default.MonadoSettings)?.Clamped() ?? new MonadoSettings();

    public void Save(MonadoSettings settings) =>
        JsonFile.Save(path, settings, MonadoSettingsJsonContext.Default.MonadoSettings);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(MonadoSettings))]
internal sealed partial class MonadoSettingsJsonContext : JsonSerializerContext;
