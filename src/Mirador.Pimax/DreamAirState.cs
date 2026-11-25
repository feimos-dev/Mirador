using System.Text.Json.Serialization;
using Mirador.Core;

namespace Mirador.Pimax;

public sealed record DreamAirState(
    string? BlobVariant = null,
    string? BlobTrackerSerial = null);

public sealed class DreamAirStateStore(string path)
{
    private readonly object gate = new();

    public DreamAirState Load()
    {
        lock (gate)
        {
            var state = JsonFile.Load(path, DreamAirJsonContext.Default.DreamAirState) ?? new DreamAirState();
            return state.BlobVariant is { } variant && !EyeBlobVariants.All.Contains(variant)
                ? state with { BlobVariant = null }
                : state;
        }
    }

    public void Save(DreamAirState state)
    {
        lock (gate)
        {
            JsonFile.Save(path, state, DreamAirJsonContext.Default.DreamAirState);
        }
    }

    public DreamAirState Update(Func<DreamAirState, DreamAirState> change)
    {
        lock (gate)
        {
            var next = change(Load());
            Save(next);
            return next;
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DreamAirState))]
internal sealed partial class DreamAirJsonContext : JsonSerializerContext;
