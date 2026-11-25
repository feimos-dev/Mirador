using System.Globalization;
using Mirador.Core.BaseStations;

namespace Mirador.Core.Lighthouse;

public sealed record UniversePick(string? Id, bool DiffersFromSteamVr);

public static class CurrentUniverse
{
    private const string V2Prefix = "LHB-";

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

    public static UniversePick FromSteam(string? steamDir, IReadOnlyList<KnownStation> stations)
    {
        var (universes, roomSetups) = SteamVrRoom.Read(steamDir);
        return Pick(universes, roomSetups, stations);
    }

    private static string? NameHex(string name)
    {
        if (!name.StartsWith(V2Prefix, StringComparison.Ordinal) || name.Length != V2Prefix.Length + 8)
        {
            return null;
        }

        var hex = name[V2Prefix.Length..];
        return uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _) ? hex : null;
    }

    private static string? SerialHex(string serial) =>
        uint.TryParse(serial, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value.ToString("X8", CultureInfo.InvariantCulture)
            : null;
}
