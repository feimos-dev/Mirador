using Mirador.Core.Modules;
using Mirador.Core.Session;

namespace Mirador.Core.BaseStations;

public sealed record StationView(KnownStation Station, bool Seen, StationPower Power, string? Problem);

public sealed record StationsSummary(string Text, StatusLevel Level, bool Busy);

public sealed partial class StationManager : ISessionHook
{
    public static readonly TimeSpan ScanTime = TimeSpan.FromSeconds(8);

    public static readonly TimeSpan FindTime = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan FollowTime = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan OffDelay = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan TryLimit = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan TryPause = TimeSpan.FromSeconds(1);

    public const int Tries = 3;

    internal static readonly TimeSpan LoadRetry = TimeSpan.FromSeconds(5);

    private const string NoAnswer = "didn't answer";
    private const string BadId = "That isn't 8 letters and digits (0–9, A–F).";
    private const string NotListed = "Mirador no longer lists that base station.";
    private static readonly string TooLong = $"Use at most {KnownStation.MaxNameLength} characters.";

    private readonly IStationRadio _radio;
    private readonly StationStore _store;
    private readonly Func<AppSettings> _settings;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;

    private readonly object _lock = new();
    private readonly object _watchGate = new();
    private readonly object _saveGate = new();
    private readonly object _liveGate = new();

    private List<Entry>? _stations;
    private long? _loadFailedAt;
    private long _version;
    private string? _saveProblem;

    private long _savedVersion;

    public StationManager(IStationRadio radio, StationStore store, Func<AppSettings> settings, TimeProvider time, Action<string> log)
    {
        _radio = radio;
        _store = store;
        _settings = settings;
        _time = time;
        _log = log;
        radio.StateChanged += Raise;
    }

    public event Action? Changed;

    public BluetoothState Bluetooth => _radio.State;

    public IReadOnlyList<StationView> Stations
    {
        get
        {
            EnsureLoaded();
            lock (_lock)
            {
                return [.. Ordered().Select(e => new StationView(e.Station, e.Seen, e.Power, e.Problem))];
            }
        }
    }

    public StationCommand? Running
    {
        get
        {
            lock (_lock)
            {
                return _running;
            }
        }
    }

    public string? SaveProblem
    {
        get
        {
            lock (_lock)
            {
                return _saveProblem;
            }
        }
    }

    public StationsSummary Summary
    {
        get
        {
            switch (_radio.State)
            {
                case BluetoothState.Missing:
                    return new StationsSummary("This PC has no Bluetooth.", StatusLevel.Info, false);
                case BluetoothState.Off:
                    return new StationsSummary("Bluetooth is off.", StatusLevel.Warning, false);
            }

            EnsureLoaded();
            lock (_lock)
            {
                var selected = Ordered().Where(e => e.Station.Selected).ToList();
                if (selected.Count == 0)
                {
                    return new StationsSummary("None selected: choose yours on the Lighthouse page.", StatusLevel.Info, false);
                }

                var now = _time.GetTimestamp();
                var shown = selected.Select(e => Shown(e, now)).ToList();
                var on = selected.Where((e, i) => e.Seen && shown[i] == StationPower.On).Count();
                var busy = _running is not null || shown.Contains(StationPower.Waking);
                var text = $"{on}/{selected.Count} Powered";
                var problems = selected.Where(e => e.Problem is not null).Select(e => e.Station.DisplayName).ToList();
                if (problems.Count > 0)
                {
                    return new StationsSummary($"{text} · {string.Join(", ", problems)} didn't answer.", StatusLevel.Warning, busy);
                }

                return new StationsSummary(text, on == selected.Count ? StatusLevel.Ok : StatusLevel.Info, busy);
            }
        }
    }

    public void SetSelected(string address, bool selected) =>
        Change(address, s => s with { Selected = selected });

    public string? Rename(string address, string text)
    {
        var name = text?.Trim() ?? string.Empty;
        if (name.Length > KnownStation.MaxNameLength)
        {
            return Find(address) is null ? NotListed : TooLong;
        }

        return Change(address, s => s with { CustomName = name.Length == 0 ? null : name });
    }

    public string? SetId(string address, string text)
    {
        var id = text?.Trim() ?? string.Empty;
        if (id.Length > 0 && !StationProtocol.TryParseV1Id(id, out _))
        {
            return Find(address) is null ? NotListed : BadId;
        }

        return Change(address, s => s with { Id = id.Length == 0 ? null : id });
    }

    public void Forget(string address)
    {
        var key = Normalize(address);
        EnsureLoaded();
        lock (_lock)
        {
            if (key is null || _stations is null || _stations.RemoveAll(e => e.Station.Address == key) == 0)
            {
                return;
            }

            _version++;
        }

        Save();
        Raise();
    }

    public StationCommand OffCommand() => StationCommand.Sleep;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? Normalize(string? address) =>
        StationProtocol.TryParseAddress(address, out var bits) ? StationProtocol.FormatAddress(bits) : null;

    private static string Reason(Exception e)
    {
        var text = e.Message.Trim().TrimEnd('.');
        return text.Length > 0 ? text : e.GetType().Name;
    }

    private StationPower Shown(Entry entry, long now) =>
        entry.Power != StationPower.On && entry.WokenAt is { } woken && _time.GetElapsedTime(woken, now) < SettleTime
            ? StationPower.Waking
            : entry.Power;

    private IEnumerable<Entry> Ordered() =>
        (_stations ?? [])
            .OrderBy(e => e.Station.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Station.Address, StringComparer.Ordinal);

    private Entry? Lookup(string address) => _stations?.Find(e => e.Station.Address == address);

    private KnownStation? Find(string address)
    {
        var key = Normalize(address);
        if (key is null)
        {
            return null;
        }

        EnsureLoaded();
        lock (_lock)
        {
            return Lookup(key)?.Station;
        }
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_stations is not null || (_loadFailedAt is { } failed && _time.GetElapsedTime(failed) < LoadRetry))
            {
                return;
            }
        }

        IReadOnlyList<KnownStation> loaded;
        try
        {
            loaded = _store.Load();
        }
        catch (Exception e)
        {
            bool first;
            lock (_lock)
            {
                first = _loadFailedAt is null;
                _loadFailedAt = _time.GetTimestamp();
            }

            if (first)
            {
                Log($"Base stations: Mirador couldn't read its base station list ({Reason(e)}); it tries again when it needs it.");
            }

            return;
        }

        lock (_lock)
        {
            _stations ??= [.. loaded.Select(s => new Entry(s))];
            _loadFailedAt = null;
        }
    }

    private string? Change(string address, Func<KnownStation, KnownStation> change)
    {
        var key = Normalize(address);
        if (key is null)
        {
            return NotListed;
        }

        EnsureLoaded();
        bool changed;
        lock (_lock)
        {
            if (Lookup(key) is not { } entry)
            {
                return NotListed;
            }

            var station = change(entry.Station);
            changed = station != entry.Station;
            if (changed)
            {
                entry.Station = station;
                _version++;
            }
        }

        var problem = Save();
        if (changed)
        {
            Raise();
        }

        return problem;
    }

    private string? Save()
    {
        string? problem;
        bool shown;
        lock (_saveGate)
        {
            KnownStation[] stations;
            long version;
            lock (_lock)
            {
                if (_stations is null || _version == _savedVersion)
                {
                    return _saveProblem;
                }

                version = _version;
                stations = [.. _stations.Select(e => e.Station)];
            }

            try
            {
                _store.Save(stations);
                _savedVersion = version;
                problem = null;
            }
            catch (Exception e)
            {
                problem = $"Mirador couldn't save its base station list ({Reason(e)}).";
            }

            lock (_lock)
            {
                shown = _saveProblem != problem;
                _saveProblem = problem;
            }
        }

        if (problem is not null)
        {
            Log($"Base stations: {problem}");
        }

        if (shown)
        {
            Raise();
        }

        return problem;
    }

    private void Raise()
    {
        if (Changed is not { } changed)
        {
            return;
        }

        foreach (var subscriber in changed.GetInvocationList())
        {
            try
            {
                ((Action)subscriber)();
            }
            catch (Exception e)
            {
                Log($"Base stations: showing a change failed ({Reason(e)})");
            }
        }
    }

    private void Log(string text)
    {
        try
        {
            _log(text);
        }
        catch (Exception)
        {
        }
    }

    private sealed class Entry(KnownStation station)
    {
        public KnownStation Station { get; set; } = station;

        public bool Seen { get; set; }

        public long LastAdvert { get; set; }

        public bool? RandomAddress { get; set; }

        public StationPower Power { get; set; }

        public byte? State { get; set; }

        public long StateSince { get; set; }

        public long? WokenAt { get; set; }

        public string? Problem { get; set; }

        public StationPower? Told { get; set; }
    }
}
