using System.Text.Json.Serialization;
using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Playspace;

/// <summary>The shape of <c>roomsetup.json</c>.</summary>
public sealed record RoomSetupList(IReadOnlyList<RoomSetup> Setups);

/// <summary>
/// Keeps Mirador's room setups in one JSON file, one per tracking origin and Lighthouse universe. Each change writes
/// the whole file aside and then moves it over the old one. Calls from several threads go one after the other.
/// </summary>
public sealed class RoomSetupStore(string path)
{
    private readonly object _lock = new();

    /// <summary>
    /// Every saved setup. A missing file gives none; one that can't be read is set aside as <c>.bad</c> and gives none.
    /// Entries without an origin name, or whose orientation isn't a rotation (a unit quaternion), are left out, and then
    /// a copy of the file is kept as <c>.bad</c>.
    /// </summary>
    public IReadOnlyList<RoomSetup> All()
    {
        lock (_lock)
        {
            return Load();
        }
    }

    /// <summary>The setup of origin <paramref name="originName"/> in <paramref name="universe"/>; null when there is none.</summary>
    public RoomSetup? Get(string originName, string? universe)
    {
        lock (_lock)
        {
            return Load().FirstOrDefault(s => Same(s, originName, universe));
        }
    }

    /// <summary>Saves <paramref name="setup"/>, replacing the one of the same origin and universe.</summary>
    public void Set(RoomSetup setup)
    {
        lock (_lock)
        {
            var setups = Load().Where(s => !Same(s, setup.OriginName, setup.Universe)).Append(setup).ToList();
            Save(setups);
        }
    }

    /// <summary>Removes the setup of origin <paramref name="originName"/> in <paramref name="universe"/>, if there is one.</summary>
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

    /// <summary>Finite numbers, and an orientation of length 1 (give or take what float rounding leaves).</summary>
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
