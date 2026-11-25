using System.Text.Json.Serialization;
using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Playspace;

public sealed record RoomSetupList(IReadOnlyList<RoomSetup> Setups);

public sealed class RoomSetupStore(string path)
{
    private readonly object _lock = new();

    public IReadOnlyList<RoomSetup> All()
    {
        lock (_lock)
        {
            return Load();
        }
    }

    public RoomSetup? Get(string originName, string? universe)
    {
        lock (_lock)
        {
            return Load().FirstOrDefault(s => Same(s, originName, universe));
        }
    }

    public void Set(RoomSetup setup)
    {
        lock (_lock)
        {
            var setups = Load().Where(s => !Same(s, setup.OriginName, setup.Universe)).Append(setup).ToList();
            Save(setups);
        }
    }

    public void Clear(string originName, string? universe)
    {
        lock (_lock)
        {
            var setups = Load();
            var left = setups.Where(s => !Same(s, originName, universe)).ToList();
            if (left.Count != setups.Count)
            {
                Save(left);
            }
        }
    }

    private static bool Same(RoomSetup setup, string originName, string? universe) =>
        string.Equals(setup.OriginName, originName, StringComparison.Ordinal)
        && string.Equals(setup.Universe, universe, StringComparison.Ordinal);

    private IReadOnlyList<RoomSetup> Load()
    {
        var changed = false;
        var list = JsonFile.Load(path, RoomSetupJsonContext.Default.RoomSetupList, l => Usable(l, out changed));
        if (list is null)
        {
            return [];
        }

        if (changed)
        {
            JsonFile.KeepCopyAsBad(path);
        }

        return list.Setups;
    }

    private static bool IsPose(MonadoPose p)
    {
        float[] all = [p.Qx, p.Qy, p.Qz, p.Qw, p.X, p.Y, p.Z];
        var length = (p.Qx * p.Qx) + (p.Qy * p.Qy) + (p.Qz * p.Qz) + (p.Qw * p.Qw);
        return all.All(float.IsFinite) && Math.Abs(length - 1) < 1e-3f;
    }

    private void Save(IReadOnlyList<RoomSetup> setups) =>
        JsonFile.Save(path, new RoomSetupList(setups), RoomSetupJsonContext.Default.RoomSetupList);

    private static RoomSetupList? Usable(RoomSetupList list, out bool changed)
    {
        changed = false;
        if (list.Setups is null)
        {
            return null;
        }

        var setups = list.Setups.Where(s => s is not null && !string.IsNullOrEmpty(s.OriginName) && IsPose(s.Offset)).ToList();
        changed = setups.Count != list.Setups.Count;
        return new RoomSetupList(setups);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(RoomSetupList))]
internal sealed partial class RoomSetupJsonContext : JsonSerializerContext;
