namespace Mirador.Core.BaseStations;

public sealed partial class StationManager
{
    public static readonly TimeSpan StallTime = TimeSpan.FromSeconds(8);

    public const int Resends = 2;

    private readonly List<Wake> _wakes = [];
    private ITimer? _wakeTimer;

    private long _wakeEpoch;

    private static bool Wanted(Entry entry, Wake wake) => entry.Station.Selected || !wake.ForSelected;

    private void WokenLocked(Entry entry, Work work, long now)
    {
        entry.WokenAt = now;
        if (work.WakeEpoch != _wakeEpoch)
        {
            return;
        }

        if (work.ResendOf is { } wake)
        {
            wake.Since = now;
            wake.Resend = null;
            return;
        }

        _wakes.RemoveAll(w => w.Address == entry.Station.Address);
        _wakes.Add(new Wake(entry.Station.Address, work.Address is null, work.FromSession, now));
    }

    private List<Work> CancelWakesLocked()
    {
        _wakeEpoch++;
        _wakes.Clear();
        _wakeTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return TakeQueuedLocked(w => w.ResendOf is not null);
    }

    private TimeSpan WakesLeftLocked(long now)
    {
        var left = TimeSpan.Zero;
        foreach (var wake in _wakes)
        {
            var wakeLeft = wake.Resend is not null ? SettleTime : SettleTime - _time.GetElapsedTime(wake.Since, now);
            if (wakeLeft > left)
            {
                left = wakeLeft;
            }
        }

        return left;
    }

    private void CheckWakes()
    {
        try
        {
            var start = false;
            lock (_lock)
            {
                var now = _time.GetTimestamp();
                TimeSpan? due = null;
                foreach (var wake in _wakes.ToList())
                {
                    if (wake.Resend is not null)
                    {
                        continue;
                    }

                    var watched = _time.GetElapsedTime(wake.Since, now);
                    if (Lookup(wake.Address) is not { } entry
                        || !Wanted(entry, wake)
                        || entry.Power == StationPower.On
                        || wake.Left == 0
                        || watched >= SettleTime)
                    {
                        _wakes.Remove(wake);
                        continue;
                    }

                    Earliest(ref due, SettleTime - watched);
                    if (entry.State != StationProtocol.StartingState)
                    {
                        continue;
                    }

                    var stuck = StallTime - _time.GetElapsedTime(Math.Max(entry.StateSince, wake.Since), now);
                    if (stuck > TimeSpan.Zero)
                    {
                        Earliest(ref due, stuck);
                        continue;
                    }

                    wake.Left--;
                    wake.Resend = new Work(WorkKind.Switch, StationCommand.On, wake.Address, CancellationToken.None, wake.FromSession, wake);
                    start |= EnqueueLocked(wake.Resend);
                }

                if (due is { } wait)
                {
                    _wakeTimer ??= _time.CreateTimer(_ => CheckWakes(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    _wakeTimer.Change(wait, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _wakeTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
            }

            if (start)
            {
                StartWorker();
            }
        }
        catch (Exception e)
        {
            Log($"Base stations: watching the base stations wake up failed ({Reason(e)})");
        }
    }

    private sealed class Wake(string address, bool forSelected, bool fromSession, long since)
    {
        public string Address { get; } = address;

        public bool ForSelected { get; } = forSelected;

        public bool FromSession { get; } = fromSession;

        public long Since { get; set; } = since;

        public int Left { get; set; } = Resends;

        public Work? Resend { get; set; }
    }
}
