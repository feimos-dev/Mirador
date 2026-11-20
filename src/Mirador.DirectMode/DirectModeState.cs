using System.Text.Json.Serialization;
using Mirador.Core;

namespace Mirador.DirectMode;

/// <summary>What Mirador remembers about direct mode.</summary>
/// <param name="LastGoodDriver">The NVIDIA driver the display check last passed with; a Start checks again after a change.</param>
public sealed record DirectModeState(string? LastGoodDriver = null);

/// <summary>Keeps the <see cref="DirectModeState"/> in one JSON file (<c>directmode.json</c>).</summary>
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
