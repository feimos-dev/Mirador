namespace Mirador.Core.BaseStations;

public sealed partial class StationManager
{
    private int _listens;
    private bool _live;
    private int _scans;
    private long _adverts;
    private bool _watching;
    private ITimer? _tickTimer;
    private ITimer? _followTimer;
    private TaskCompletionSource _newlyHeard = NewSignal();

    private IDisposable? _watch;

    public bool Looking
    {
        get
        {
            lock (_lock)
            {
                return _scans > 0 && _watching;
            }
        }
    }

    public IDisposable Listen()
    {
        lock (_liveGate)
        {
            var live = _settings().BaseStationsState == StationStateMode.Live;
            lock (_lock)
            {
                _listens++;
                _live = live;
            }
        }

        UpdateWatch();
        return new Listening(this);
    }

    public void SettingsChanged()
    {
        lock (_liveGate)
        {
            var live = _settings().BaseStationsState == StationStateMode.Live;
            lock (_lock)
            {
                _live = live;
            }
        }

        UpdateWatch();
    }

    public async Task ScanAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureLoaded();
        long since;
        lock (_lock)
        {
            _scans++;
            since = _adverts;
        }

        UpdateWatch();
        Raise();
        var missed = CantListen();
        var done = false;
        try
        {
            await Task.Delay(ScanTime, _time, ct).ConfigureAwait(false);
            done = true;
        }
        finally
        {
            if (done)
            {
                missed ??= CantListen();
            }

            int heard = 0, count = 0;
            lock (_lock)
            {
                _scans--;
                if (done && missed is null)
                {
                    foreach (var entry in _stations ?? [])
                    {
                        count++;
                        if (entry.LastAdvert > since)
                        {
                            heard++;
                        }
                        else
                        {
                            entry.Seen = false;
                        }
                    }
                }
            }

            if (done)
            {
                Log(missed is null
                    ? $"Base stations: scanned for {ScanTime.TotalSeconds:0} s, heard {heard} of {count}"
                    : $"Base stations: couldn't scan ({missed})");
            }

            Follow();
            Raise();
        }
    }

    private string? CantListen()
    {
        var bluetooth = _radio.State;
        if (bluetooth != BluetoothState.Ready)
        {
            return Unusable(bluetooth);
        }

        lock (_lock)
        {
            return _watching ? null : "Mirador couldn't listen for the base stations";
        }
    }

    private void Heard(Advert advert)
    {
        try
        {
            if (StationProtocol.VersionOf(advert.Name) is not { } version)
            {
                return;
            }

            var address = StationProtocol.FormatAddress(advert.Address);
            EnsureLoaded();
            bool save = false, changed = false, stateChanged = false;
            TaskCompletionSource? newlyHeard = null;
            lock (_lock)
            {
                if (_stations is null)
                {
                    return;
                }

                var now = _time.GetTimestamp();
                var entry = Lookup(address);
                if (entry is null)
                {
                    entry = new Entry(new KnownStation(address, advert.Name));
                    _stations.Add(entry);
                    _version++;
                    save = changed = true;
                }
                else if (entry.Station.Name != advert.Name)
                {
                    entry.Station = entry.Station with { Name = advert.Name };
                    _version++;
                    save = changed = true;
                }

                changed |= !entry.Seen;
                entry.Seen = true;
                entry.LastAdvert = ++_adverts;
                if (entry.RandomAddress is null)
                {
                    newlyHeard = _newlyHeard;
                    _newlyHeard = NewSignal();
                }

                entry.RandomAddress = advert.RandomAddress;

                if (version == StationVersion.V2 && advert.ValveData is { } data)
                {
                    var power = StationProtocol.AdvertState(data);
                    changed |= power != entry.Power;
                    entry.Power = power;
                    var state = StationProtocol.AdvertStateByte(data);
                    if (state != entry.State)
                    {
                        entry.State = state;
                        entry.StateSince = now;
                        stateChanged = true;
                    }

                    if (power == StationPower.On)
                    {
                        entry.WokenAt = null;
                    }

                    if (entry.Problem is not null && entry.Told == power)
                    {
                        entry.Problem = null;
                        entry.Told = null;
                        changed = true;
                    }
                }
            }

            newlyHeard?.TrySetResult();
            if (save)
            {
                Save();
            }

            if (stateChanged)
            {
                CheckWakes();

                FollowSoon();
            }

            if (changed)
            {
                ScheduleTick();
                Raise();
            }
        }
        catch (Exception e)
        {
            Log($"Base stations: an advert couldn't be read ({Reason(e)})");
        }
    }

    private void UpdateWatch()
    {
        lock (_watchGate)
        {
            bool want;
            lock (_lock)
            {
                want = _scans > 0 || _commandRunning || _settling || _live || FollowLocked(_time.GetTimestamp()) is not null;
            }

            if (want == (_watch is not null))
            {
                return;
            }

            if (want)
            {
                try
                {
                    _watch = _radio.Watch(Heard);
                }
                catch (Exception e)
                {
                    Log($"Base stations: Mirador couldn't listen for the base stations ({Reason(e)})");
                    return;
                }

                lock (_lock)
                {
                    _watching = true;
                }
            }
            else
            {
                var watch = _watch!;
                _watch = null;
                lock (_lock)
                {
                    _watching = false;
                }

                try
                {
                    watch.Dispose();
                }
                catch (Exception e)
                {
                    Log($"Base stations: listening didn't stop cleanly ({Reason(e)})");
                }
            }
        }

        Raise();
    }

    private TimeSpan? FollowLocked(long now)
    {
        TimeSpan? due = null;
        foreach (var entry in _stations ?? [])
        {
            if (entry.Power == StationPower.Waking)
            {
                Earliest(ref due, FollowTime - _time.GetElapsedTime(entry.StateSince, now));
            }
        }

        return due;
    }

    private void FollowSoon() => FollowTimer().Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);

    private void Follow()
    {
        try
        {
            UpdateWatch();
            TimeSpan? due;
            lock (_lock)
            {
                due = FollowLocked(_time.GetTimestamp());
            }

            if (due is { } wait)
            {
                FollowTimer().Change(wait, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception e)
        {
            Log($"Base stations: {Reason(e)}");
        }
    }

    private ITimer FollowTimer()
    {
        lock (_lock)
        {
            return _followTimer ??= _time.CreateTimer(_ => Follow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void StopListening()
    {
        lock (_lock)
        {
            _listens--;
        }

        UpdateWatch();
    }

    private static void Earliest(ref TimeSpan? due, TimeSpan left)
    {
        if (left > TimeSpan.Zero && (due is null || left < due))
        {
            due = left;
        }
    }

    private void ScheduleTick()
    {
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            TimeSpan? due = null;
            foreach (var entry in _stations ?? [])
            {
                if (entry.WokenAt is { } woken)
                {
                    Earliest(ref due, SettleTime - _time.GetElapsedTime(woken, now));
                }
            }

            if (due is { } wait)
            {
                _tickTimer ??= _time.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _tickTimer.Change(wait, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _tickTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Tick()
    {
        try
        {
            ScheduleTick();
            Raise();
        }
        catch (Exception e)
        {
            Log($"Base stations: {Reason(e)}");
        }
    }

    private sealed class Listening(StationManager owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.StopListening();
            }
        }
    }
}
