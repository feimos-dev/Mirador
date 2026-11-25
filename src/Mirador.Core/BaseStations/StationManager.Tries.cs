namespace Mirador.Core.BaseStations;

public sealed partial class StationManager
{
    private async Task TryAllAsync(List<string> targets, Work work)
    {
        var pending = new List<string>(targets);
        var tried = new HashSet<string>();
        var reasons = new Dictionary<string, string>();
        var stop = false;
        for (var round = 1; round <= Tries && pending.Count > 0 && !stop; round++)
        {
            if (round > 1)
            {
                await Task.Delay(TryPause, _time, work.Token).ConfigureAwait(false);
            }

            foreach (var address in pending.ToList())
            {
                KnownStation station;
                bool random;
                lock (_lock)
                {
                    if (Lookup(address) is not { } entry || !Wanted(entry, work))
                    {
                        pending.Remove(address);
                        continue;
                    }

                    station = entry.Station;

                    random = entry.RandomAddress ?? true;
                }

                var bluetooth = _radio.State;
                if (bluetooth != BluetoothState.Ready)
                {
                    foreach (var rest in pending.SkipWhile(a => a != address))
                    {
                        reasons[rest] = Unusable(bluetooth);
                    }

                    stop = true;
                    break;
                }

                tried.Add(address);
                if (await TryOnceAsync(station, random, work).ConfigureAwait(false) is { } reason)
                {
                    reasons[address] = reason;
                }
                else
                {
                    pending.Remove(address);
                    Answered(station, work);
                }
            }
        }

        var lines = new List<string>();
        var named = false;
        lock (_lock)
        {
            foreach (var address in pending)
            {
                if (Lookup(address) is not { } entry || !Wanted(entry, work))
                {
                    continue;
                }

                var reason = reasons.GetValueOrDefault(address, "no reason given");
                var station = Line(work, entry.Station.DisplayName, address);
                if (tried.Contains(address))
                {
                    entry.Problem = NoAnswer;
                    entry.Told = work.Kind == WorkKind.Switch ? Shows(work.Command) : null;
                    named = true;
                    lines.Add($"{station}: didn't answer ({reason})");
                }
                else
                {
                    lines.Add($"{station}: not sent ({reason})");
                }
            }
        }

        foreach (var line in lines)
        {
            Log(line);
        }

        if (named)
        {
            Raise();
        }
    }

    private static string Line(Work work, string display, string address) =>
        $"Base stations: {Name(work)} {display} ({address}){(work.ResendOf is null ? string.Empty : " again (stuck waking up)")}";

    private static StationPower? Shows(StationCommand command) =>
        command switch
        {
            StationCommand.On => StationPower.On,
            StationCommand.Sleep => StationPower.Asleep,
            _ => null,
        };

    private void Answered(KnownStation station, Work work)
    {
        var display = station.DisplayName;
        var woken = false;
        lock (_lock)
        {
            if (Lookup(station.Address) is { } entry)
            {
                entry.Problem = null;
                entry.Told = null;
                display = entry.Station.DisplayName;
                if (work.Kind == WorkKind.Switch)
                {
                    if (StationProtocol.VersionOf(entry.Station.Name) == StationVersion.V1)
                    {
                        entry.Power = work.Command == StationCommand.On ? StationPower.On : StationPower.Asleep;
                    }
                    else if (work.IsOn)
                    {
                        WokenLocked(entry, work, _time.GetTimestamp());
                        woken = true;
                    }
                    else
                    {
                        entry.WokenAt = null;
                        _wakes.RemoveAll(w => w.Address == entry.Station.Address);
                    }
                }
            }
        }

        Log($"Base stations: {Name(work)} {display} ({station.Address}): {(work.ResendOf is null ? "done" : "sent again (stuck waking up)")}");
        if (woken)
        {
            CheckWakes();
        }

        ScheduleTick();
        Raise();
    }

    private async Task<string?> TryOnceAsync(KnownStation station, bool random, Work work)
    {
        if (!StationProtocol.TryParseAddress(station.Address, out var address))
        {
            return "its address can't be read";
        }

        var stop = new Stopper(work.Token);
        var attempt = Task.Run(() => AttemptAsync(address, random, station, work, stop.Token));
        _ = attempt.ContinueWith(_ => stop.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        try
        {
            return await attempt.WaitAsync(TryLimit, _time, work.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stop.Cancel();
            return $"no answer within {TryLimit.TotalSeconds:0} seconds";
        }
        catch (OperationCanceledException)
        {
            stop.Cancel();
            throw;
        }
    }

    private async Task<string?> AttemptAsync(ulong address, bool random, KnownStation station, Work work, CancellationToken ct)
    {
        try
        {
            var link = await _radio.ConnectAsync(address, random, ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                await WriteAsync(link, station, work, ct).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await link.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return "stopped";
        }
        catch (Exception e)
        {
            return Reason(e);
        }
    }

    private static async Task WriteAsync(IStationLink link, KnownStation station, Work work, CancellationToken ct)
    {
        switch (StationProtocol.VersionOf(station.Name), work.Kind)
        {
            case (StationVersion.V2, WorkKind.Identify):
                await link.WriteAsync(StationProtocol.V2Service, StationProtocol.V2IdentifyCharacteristic, StationProtocol.V2IdentifyValue, ct)
                    .ConfigureAwait(false);
                break;
            case (StationVersion.V2, WorkKind.Switch):
                foreach (var value in StationProtocol.V2Writes(work.Command))
                {
                    await link.WriteAsync(StationProtocol.V2Service, StationProtocol.V2PowerCharacteristic, value, ct).ConfigureAwait(false);
                }

                break;
            case (StationVersion.V1, WorkKind.Switch):
                uint? id = StationProtocol.TryParseV1Id(station.Id, out var parsed) ? parsed : null;
                await link.WriteAsync(StationProtocol.V1Service, StationProtocol.V1PowerCharacteristic, StationProtocol.V1Write(work.Command, id), ct)
                    .ConfigureAwait(false);
                break;
            default:
                throw new StationException("Mirador can't send that to this base station.");
        }
    }

    private sealed class Stopper
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        public Stopper(CancellationToken outer)
        {
            _source = CancellationTokenSource.CreateLinkedTokenSource(outer);
            Token = _source.Token;
        }

        public CancellationToken Token { get; }

        public void Cancel()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    _source.Cancel();
                }
                catch (Exception)
                {
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _source.Dispose();
            }
        }
    }
}
