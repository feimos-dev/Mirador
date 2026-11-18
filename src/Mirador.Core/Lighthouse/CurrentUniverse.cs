using System.Globalization;
using Mirador.Core.BaseStations;

namespace Mirador.Core.Lighthouse;

/// <summary>The Lighthouse universe in use.</summary>
/// <param name="Id">The universe's id; null when there is none to go by.</param>
/// <param name="DiffersFromSteamVr">
/// Monado's <c>steamvr_lh</c> driver takes its room setup from another universe, so SteamVR's room setup of another
/// space is used.
/// </param>
public sealed record UniversePick(string? Id, bool DiffersFromSteamVr);

/// <summary>Works out the Lighthouse universe in use from the base stations Mirador knows.</summary>
public static class CurrentUniverse
{
    private const string V2Prefix = "LHB-";

    /// <summary>
    /// The universe that holds every second-generation station Mirador knows and SteamVR has seen in some universe. A
    /// station's Bluetooth name is <c>LHB-</c> and its <c>base_serial_number</c> as 8 hex digits. First-generation
    /// stations aren't named that way and don't count. When no such station is known, or not exactly one universe
    /// holds them all, the universe is the one <c>steamvr_lh</c> picks.
    /// </summary>
    public static UniversePick Pick(IReadOnlyList<LighthouseUniverse> universes, IReadOnlySet<string> roomSetups, IReadOnlyList<KnownStation> stations)
    {
        var steamVr = SteamVrRoom.SteamVrLhPick(universes, roomSetups);

        var serialsPerUniverse = universes
            .Select(u => (u.Id, Serials: u.StationSerials.Select(SerialHex).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .ToList();
        var seen = stations
            .Select(s => NameHex(s.Name))
            .OfType<string>()
            .Where(hex => serialsPerUniverse.Any(u => u.Serials.Contains(hex)))
            .ToList();
        if (seen.Count == 0)
        {
            return new UniversePick(steamVr, false);
        }

        var holding = serialsPerUniverse.Where(u => seen.All(u.Serials.Contains)).Select(u => u.Id).Distinct().ToList();
        if (holding.Count != 1)
        {
            return new UniversePick(steamVr, false);
        }

        var current = holding[0];
        return new UniversePick(current, steamVr is not null && steamVr != current);
    }

    /// <summary>
    /// <see cref="Pick"/> on Steam's <c>lighthousedb.json</c> and <c>chaperone_info.vrchap</c> as they are now. A file
    /// that is missing or can't be read counts as empty; without Steam (<paramref name="steamDir"/> null) there is no
    /// universe.
    /// </summary>
    public static UniversePick FromSteam(string? steamDir, IReadOnlyList<KnownStation> stations)
    {
        if (steamDir is null)
        {
            return new UniversePick(null, false);
        }

        return Pick(
            SteamVrRoom.Universes(ReadOrEmpty(SteamVrRoom.LighthousedbPath(steamDir))),
            SteamVrRoom.RoomSetups(ReadOrEmpty(SteamVrRoom.ChaperoneInfoPath(steamDir))),
            stations);
    }

    /// <summary>The file's text; empty when it is missing or can't be read. Shared, as SteamVR may be writing it.</summary>
    private static string ReadOrEmpty(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "";
        }
    }

    /// <summary>The 8 hex digits of a second-generation station's name (<c>LHB-XXXXXXXX</c>); null for any other name.</summary>
    private static string? NameHex(string name)
    {
        if (!name.StartsWith(V2Prefix, StringComparison.Ordinal) || name.Length != V2Prefix.Length + 8)
        {
            return null;
        }

        var hex = name[V2Prefix.Length..];
        return uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _) ? hex : null;
    }

    /// <summary>A <c>base_serial_number</c> (a decimal integer) as 8 hex digits; null when it isn't one.</summary>
    private static string? SerialHex(string serial) =>
        uint.TryParse(serial, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value.ToString("X8", CultureInfo.InvariantCulture)
            : null;
}
