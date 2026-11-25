using System.Text.Json.Serialization;
using Mirador.Core;

namespace Mirador.DirectMode;

public sealed record DirectModeState(string? LastGoodDriver = null);

public sealed class DirectModeStateStore(string path)
{
    private readonly object gate = new();

    public DirectModeState Load()
    {
        lock (gate)
        {
            return JsonFile.Load(path, DirectModeJsonContext.Default.DirectModeState) ?? new DirectModeState();
        }
    }

    public void Save(DirectModeState state)
    {
        lock (gate)
        {
            JsonFile.Save(path, state, DirectModeJsonContext.Default.DirectModeState);
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DirectModeState))]
internal sealed partial class DirectModeJsonContext : JsonSerializerContext;
