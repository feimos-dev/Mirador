using System.Text.Json.Serialization;

namespace Mirador.Core.BaseStations;

public sealed record KnownStation(string Address, string Name, bool Selected = false, string? CustomName = null, string? Id = null)
{
    public const int MaxNameLength = 40;

    [JsonIgnore]
    public StationVersion Version =>
        StationProtocol.VersionOf(Name) ?? throw new InvalidOperationException($"\"{Name}\" is not the name of a base station.");

    [JsonIgnore]
    public string DisplayName => CustomName ?? Name;

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Address = {Address}, Name = {Name}, Selected = {Selected}, CustomName = {CustomName}, Id = {Id}");
        return true;
    }
}

public sealed record StationList(IReadOnlyList<KnownStation> Stations);

public sealed class StationStore(string path)
{
    private readonly object _lock = new();

    public IReadOnlyList<KnownStation> Load()
    {
        lock (_lock)
        {
            var changed = false;
            var list = JsonFile.Load(path, StationsJsonContext.Default.StationList, l => Usable(l, out changed));
            if (list is null)
            {
                return [];
            }

            if (changed)
            {
                JsonFile.KeepCopyAsBad(path);
            }

            return list.Stations;
        }
    }

    public void Save(IReadOnlyList<KnownStation> stations)
    {
        lock (_lock)
        {
            JsonFile.Save(path, new StationList(stations), StationsJsonContext.Default.StationList);
        }
    }

    private static StationList? Usable(StationList list, out bool changed)
    {
        changed = false;
        if (list.Stations is null)
        {
            return null;
        }

        var seen = new HashSet<ulong>();
        var stations = new List<KnownStation>();
        foreach (var s in list.Stations)
        {
            if (s is null
                || !StationProtocol.TryParseAddress(s.Address, out var address)
                || string.IsNullOrWhiteSpace(s.Name)
                || StationProtocol.VersionOf(s.Name) is null
                || !seen.Add(address))
            {
                changed = true;
                continue;
            }

            var id = StationProtocol.TryParseV1Id(s.Id, out _) ? s.Id : null;
            var customName = CleanName(s.CustomName);
            changed |= id != s.Id || customName != s.CustomName;
            stations.Add(s with { Address = StationProtocol.FormatAddress(address), Id = id, CustomName = customName });
        }

        return new StationList(stations);
    }

    private static string? CleanName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length <= KnownStation.MaxNameLength)
        {
            return trimmed;
        }

        var length = char.IsHighSurrogate(trimmed[KnownStation.MaxNameLength - 1]) ? KnownStation.MaxNameLength - 1 : KnownStation.MaxNameLength;
        return trimmed[..length];
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(StationList))]
internal sealed partial class StationsJsonContext : JsonSerializerContext;
