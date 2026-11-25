using Mirador.Core.Modules;
using Mirador.Core.Profiles;

namespace Mirador.Core.BaseStations;

public sealed partial class StationManager
{
    private const string Quitting = "Mirador is quitting";

    private readonly List<Work> _queue = [];
    private bool _working;
    private TaskCompletionSource _idle = Idle();
    private Work? _current;
    private StationCommand? _running;
    private bool _commandRunning;
    private bool _settling;
    private long _settleSince;
    private ITimer? _settleTimer;
    private ITimer? _offTimer;
    private object? _offToken;

    private bool _offSentAtEnding;

    private bool _quitting;

    private bool _quitOff;

    private enum WorkKind
    {
        Switch,
        Identify,
    }

    public bool OffPending
    {
        get
        {
            lock (_lock)
            {
                return _offToken is not null || SessionOffOutLocked();
            }
        }
    }

    public bool OffAtQuitWanted
    {
        get
        {
            if (!_settings().BaseStationsOffAtMiradorQuit)
            {
                return false;
            }

            EnsureLoaded();
            lock (_lock)
            {
                return AnySelectedLocked();
            }
        }
    }

    public bool OffFollows(Profile profile, IHeadsetModule? module) =>
        profile.UsesBaseStations(module?.UsesSteamVrLighthouse ?? false) && _settings().BaseStationsOffAtStop;

    public async Task StartupAsync(CancellationToken ct)
    {
        try
        {
            SettingsChanged();
            await ScanAsync(ct).ConfigureAwait(false);
            if (!_settings().BaseStationsOnAtMiradorStart)
            {
                return;
            }

            Log("Base stations: switching on as Mirador starts");
            _ = Ask(new Work(WorkKind.Switch, StationCommand.On, null, ct, fromStartup: true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Log($"Base stations: Mirador couldn't follow its own start ({Reason(e)})");
        }
    }

    public Task SwitchSelectedAsync(StationCommand command, CancellationToken ct) =>
        Ask(new Work(WorkKind.Switch, command, null, ct));

    public Task SwitchAsync(string address, StationCommand command, CancellationToken ct) =>
        Ask(new Work(WorkKind.Switch, command, Normalize(address) ?? address, ct));

    public Task IdentifyAsync(string address, CancellationToken ct) =>
        Ask(new Work(WorkKind.Identify, StationCommand.On, Normalize(address) ?? address, ct));

    public void Started(Profile profile, IHeadsetModule? module)
    {
        try
        {
            lock (_lock)
            {
                _offSentAtEnding = false;
            }

            if (!profile.UsesBaseStations(module?.UsesSteamVrLighthouse ?? false))
            {
                return;
            }

            var on = _settings().BaseStationsOnAtStart;
            List<Work> dropped;
            bool delayed, start = false;
            lock (_lock)
            {
                dropped = TakeQueuedLocked(w => w.IsSessionOff && !w.AtQuit);
                dropped.AddRange(CancelWakesLocked());
                delayed = CancelDelayedOffLocked();
                if (on)
                {
                    start = EnqueueLocked(new Work(WorkKind.Switch, StationCommand.On, null, CancellationToken.None, fromSession: true));
                }
            }

            Dropped(dropped, delayed, "Monado started again");
            if (start)
            {
                StartWorker();
            }
        }
        catch (Exception e)
        {
            Log($"Base stations: Mirador couldn't follow Monado's start ({Reason(e)})");
        }
    }

    public void Ended(Profile profile, IHeadsetModule? module)
    {
        try
        {
            if (!OffFollows(profile, module))
            {
                return;
            }

            bool ending, quit;
            lock (_lock)
            {
                ending = _offSentAtEnding;
                quit = _quitOff;
                if (!ending && !quit)
                {
                    CancelDelayedOffLocked();
                    var token = new object();
                    _offToken = token;
                    _offTimer = _time.CreateTimer(OffDue, token, OffDelay, Timeout.InfiniteTimeSpan);
                }
            }

            if (ending)
            {
                Log("Base stations: the off after Monado stopped went out already (Windows is ending the session)");
            }
            else if (quit)
            {
                Log("Base stations: the off after Monado stopped isn't sent (Mirador's off as it quits went out already)");
            }
        }
        catch (Exception e)
        {
            Log($"Base stations: Mirador couldn't follow Monado's stop ({Reason(e)})");
        }
    }

    public void EndingNow(Profile profile, IHeadsetModule? module)
    {
        try
        {
            if (!OffFollows(profile, module))
            {
                return;
            }

            var command = OffCommand();
            List<Work> dropped;
            var start = false;
            lock (_lock)
            {
                dropped = CancelWakesLocked();
                CancelDelayedOffLocked();
                _offSentAtEnding = true;
                if (!SessionOffOutLocked() && !_quitOff)
                {
                    start = EnqueueLocked(new Work(WorkKind.Switch, command, null, CancellationToken.None, fromSession: true));
                }
            }

            Dropped(dropped, false, "Windows is ending the session");
            if (start)
            {
                StartWorker();
            }
        }
        catch (Exception e)
        {
            Log($"Base stations: Mirador couldn't switch the base stations off as Windows ends the session ({Reason(e)})");
        }
    }

    public void QuittingNow()
    {
        try
        {
            _ = Quit();
        }
        catch (Exception e)
        {
            Log($"Base stations: Mirador couldn't switch the base stations for its quit ({Reason(e)})");
        }
    }

    public async Task FlushAsync(TimeSpan limit)
    {
        try
        {
            await Quit().WaitAsync(limit, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log($"Base stations: Mirador stopped waiting for the base stations after {limit.TotalSeconds:0} s");
        }
        catch (Exception e)
        {
            Log($"Base stations: Mirador couldn't finish with the base stations ({Reason(e)})");
        }
    }

    private Task Quit()
    {
        var command = OffCommand();
        var quitOff = _settings().BaseStationsOffAtMiradorQuit;
        EnsureLoaded();
        List<Work> dropped;
        Work? stopping;
        var start = false;
        string? quit = null;
        Task idle;
        lock (_lock)
        {
            _quitting = true;
            dropped = CancelWakesLocked();
            dropped.AddRange(TakeQueuedLocked(w => w.IsOn));
            stopping = _current is { IsOn: true } running ? running : null;
            if (CancelDelayedOffLocked())
            {
                start = EnqueueLocked(new Work(WorkKind.Switch, command, null, CancellationToken.None, fromSession: true));
            }

            if (quitOff && !_quitOff && AnySelectedLocked())
            {
                _quitOff = true;
                if (_offSentAtEnding || SessionOffOutLocked())
                {
                    foreach (var work in _queue.Where(w => w.IsSessionOff))
                    {
                        work.AtQuit = true;
                    }

                    if (_current is { IsSessionOff: true } sending)
                    {
                        sending.AtQuit = true;
                    }

                    quit = "Base stations: the off after Monado stopped is the one as Mirador quits";
                }
                else
                {
                    start |= EnqueueLocked(new Work(WorkKind.Switch, command, null, CancellationToken.None, atQuit: true));
                    quit = "Base stations: switching off as Mirador quits";
                }
            }

            idle = _idle.Task;
        }

        Dropped(dropped, false, Quitting);
        if (quit is not null)
        {
            Log(quit);
        }

        if (stopping is not null)
        {
            _ = Task.Run(stopping.Stop);
        }

        if (start)
        {
            StartWorker();
        }

        return idle;
    }

    private static TaskCompletionSource Idle()
    {
        var idle = NewSignal();
        idle.SetResult();
        return idle;
    }

    private static string Name(Work work) => work.Kind == WorkKind.Identify ? "Identify" : work.Command.ToString();

    private static string Unusable(BluetoothState state) =>
        state == BluetoothState.Missing ? "this PC has no Bluetooth" : "Bluetooth is off";

    private static bool Wanted(Entry entry, Work work) =>
        entry.Station.Selected || (work.Address is not null && work.ResendOf is not { ForSelected: true });

    private Task Ask(Work work)
    {
        if (work.Ct.IsCancellationRequested)
        {
            work.Release();
            return Task.FromCanceled(work.Ct);
        }

        work.Registration = work.Ct.Register(() => Withdraw(work));
        List<Work> dropped = [];
        bool delayed = false, start = false, refused;
        lock (_lock)
        {
            refused = work.FromStartup && _quitting;
            if (!refused)
            {
                if (work.Kind == WorkKind.Switch)
                {
                    dropped = TakeQueuedLocked(w => !w.AtQuit && (w.IsSessionOff || (w.IsOff && !w.FromSession && w.Address == work.Address)));
                    dropped.AddRange(CancelWakesLocked());
                    delayed = CancelDelayedOffLocked();
                }

                start = EnqueueLocked(work);
            }
        }

        if (refused)
        {
            work.Release();
            work.Done.TrySetResult();
            Log($"Base stations: {Name(work)} not sent ({Quitting})");
            return work.Done.Task;
        }

        Dropped(dropped, delayed, "a later command replaced it");
        if (start)
        {
            StartWorker();
        }

        return work.Done.Task;
    }

    private void Withdraw(Work work)
    {
        bool removed;
        lock (_lock)
        {
            removed = _queue.Remove(work);
        }

        if (removed)
        {
            work.Release();
            work.Done.TrySetCanceled(work.Ct);
            Log($"Base stations: {Name(work)} cancelled");
        }
    }

    private bool AnySelectedLocked() => _stations?.Exists(e => e.Station.Selected) ?? false;

    private bool SessionOffOutLocked() => _queue.Exists(w => w.IsSessionOff) || _current is { IsSessionOff: true };

    private List<Work> TakeQueuedLocked(Predicate<Work> match)
    {
        var taken = _queue.FindAll(match);
        _queue.RemoveAll(match);
        return taken;
    }

    private bool CancelDelayedOffLocked()
    {
        if (_offToken is null)
        {
            return false;
        }

        _offTimer?.Dispose();
        _offTimer = null;
        _offToken = null;
        return true;
    }

    private bool EnqueueLocked(Work work)
    {
        work.WakeEpoch = _wakeEpoch;
        _queue.Add(work);
        if (_working)
        {
            return false;
        }

        _working = true;
        _idle = NewSignal();
        return true;
    }

    private void Dropped(List<Work> dropped, bool delayedOff, string why)
    {
        foreach (var work in dropped)
        {
            work.Release();
            work.Done.TrySetResult();
            Log(work.ResendOf is null
                ? $"Base stations: {Name(work)} not sent ({why})"
                : $"Base stations: {Name(work)} {work.Address}: not sent again ({why})");
        }

        if (delayedOff)
        {
            Log($"Base stations: the off after Monado stopped isn't sent ({why})");
        }
    }

    private void OffDue(object? token)
    {
        try
        {
            var command = OffCommand();
            List<Work> dropped;
            bool start;
            lock (_lock)
            {
                if (token is null || !ReferenceEquals(token, _offToken))
                {
                    return;
                }

                CancelDelayedOffLocked();
                dropped = CancelWakesLocked();
                start = EnqueueLocked(new Work(WorkKind.Switch, command, null, CancellationToken.None, fromSession: true));
            }

            Dropped(dropped, false, "the off after Monado stopped replaced it");
            if (start)
            {
                StartWorker();
            }
        }
        catch (Exception e)
        {
            Log($"Base stations: the off after Monado stopped couldn't be queued ({Reason(e)})");
        }
    }

    private void StartWorker() => _ = Task.Run(WorkAsync);

    private async Task WorkAsync()
    {
        while (true)
        {
            Work? work = null;
            TaskCompletionSource? idle = null;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    _working = false;
                    idle = _idle;
                }
                else
                {
                    work = _queue[0];
                    _queue.RemoveAt(0);
                    _current = work;
                }
            }

            if (work is null)
            {
                idle!.TrySetResult();
                return;
            }

            var cancelled = false;
            try
            {
                work.Token.ThrowIfCancellationRequested();
                await RunAsync(work).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (work.Ct.IsCancellationRequested)
            {
                Log($"Base stations: {Name(work)} cancelled");
                cancelled = true;
            }
            catch (OperationCanceledException) when (work.Stopped)
            {
                Log($"Base stations: {Name(work)} stopped ({Quitting})");
            }
            catch (Exception e)
            {
                Log($"Base stations: {Name(work)} failed ({Reason(e)})");
            }
            finally
            {
                lock (_lock)
                {
                    _current = null;

                    if (work.ResendOf is { } wake && ReferenceEquals(wake.Resend, work))
                    {
                        _wakes.Remove(wake);
                    }
                }

                work.Release();
            }

            if (cancelled)
            {
                work.Done.TrySetCanceled(work.Ct);
            }
            else
            {
                work.Done.TrySetResult();
            }
        }
    }

    private async Task RunAsync(Work work)
    {
        var name = Name(work);
        var bluetooth = _radio.State;
        if (bluetooth != BluetoothState.Ready)
        {
            Log($"Base stations: {name} not sent ({Unusable(bluetooth)})");
            return;
        }

        EnsureLoaded();
        var targets = new List<string>();
        var notSent = new List<string>();
        lock (_lock)
        {
            var candidates = work.Address is null
                ? Ordered().Where(e => e.Station.Selected).ToList()
                : Lookup(work.Address) is { } one ? [one] : new List<Entry>();
            foreach (var entry in candidates)
            {
                var line = Line(work, entry.Station.DisplayName, entry.Station.Address);
                if (!Wanted(entry, work))
                {
                    notSent.Add($"{line}: not sent (it is no longer selected)");
                }
                else if (work.Kind == WorkKind.Identify && StationProtocol.VersionOf(entry.Station.Name) != StationVersion.V2)
                {
                    notSent.Add($"{line}: not sent (1.0 base stations can't blink)");
                }
                else if (work.Kind == WorkKind.Identify && entry.Power != StationPower.On)
                {
                    notSent.Add($"{line}: not sent (it isn't on)");
                }
                else
                {
                    targets.Add(entry.Station.Address);
                }
            }
        }

        foreach (var line in notSent)
        {
            Log(line);
        }

        if (targets.Count == 0)
        {
            if (notSent.Count == 0)
            {
                Log(work.Address is null
                    ? $"Base stations: {name} not sent (no base station is selected)"
                    : $"Base stations: {name} not sent (Mirador doesn't list {work.Address})");
            }

            return;
        }

        lock (_lock)
        {
            _running = work.Kind == WorkKind.Switch ? work.Command : null;
            _commandRunning = true;
        }

        UpdateWatch();
        Raise();
        try
        {
            await FindAsync(targets, work).ConfigureAwait(false);
            await TryAllAsync(targets, work).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _running = null;
                _commandRunning = false;
                _settling = true;
                _settleSince = _time.GetTimestamp();
                _settleTimer ??= _time.CreateTimer(_ => Settled(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _settleTimer.Change(SettleTime, Timeout.InfiniteTimeSpan);
            }

            UpdateWatch();
            Raise();
        }
    }

    private async Task FindAsync(List<string> targets, Work work)
    {
        var start = _time.GetTimestamp();
        while (true)
        {
            Task heard;
            lock (_lock)
            {
                if (targets.All(a => Lookup(a) is not { } e || !Wanted(e, work) || e.RandomAddress is not null))
                {
                    return;
                }

                heard = _newlyHeard.Task;
            }

            var left = FindTime - _time.GetElapsedTime(start);
            if (left <= TimeSpan.Zero)
            {
                return;
            }

            try
            {
                await heard.WaitAsync(left, _time, work.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return;
            }
        }
    }

    private void Settled()
    {
        try
        {
            lock (_lock)
            {
                var now = _time.GetTimestamp();
                var left = SettleTime - _time.GetElapsedTime(_settleSince, now);
                var wakes = WakesLeftLocked(now);
                if (wakes > left)
                {
                    left = wakes;
                }

                if (left > TimeSpan.Zero)
                {
                    _settleTimer?.Change(left, Timeout.InfiniteTimeSpan);
                    return;
                }

                _settling = false;
            }

            Follow();
        }
        catch (Exception e)
        {
            Log($"Base stations: {Reason(e)}");
        }
    }

    private sealed class Work(
        WorkKind kind,
        StationCommand command,
        string? address,
        CancellationToken ct,
        bool fromSession = false,
        Wake? resendOf = null,
        bool atQuit = false,
        bool fromStartup = false)
    {
        private readonly Stopper _stopper = new(ct);
        private volatile bool _stopped;

        public WorkKind Kind { get; } = kind;

        public StationCommand Command { get; } = command;

        public string? Address { get; } = address;

        public CancellationToken Ct { get; } = ct;

        public CancellationToken Token => _stopper.Token;

        public bool FromSession { get; } = fromSession;

        public bool AtQuit { get; set; } = atQuit;

        public bool FromStartup { get; } = fromStartup;

        public Wake? ResendOf { get; } = resendOf;

        public long WakeEpoch { get; set; }

        public TaskCompletionSource Done { get; } = NewSignal();

        public CancellationTokenRegistration Registration { get; set; }

        public bool Stopped => _stopped;

        public bool IsOn => Kind == WorkKind.Switch && Command == StationCommand.On;

        public bool IsOff => Kind == WorkKind.Switch && Command != StationCommand.On;

        public bool IsSessionOff => FromSession && IsOff;

        public void Stop()
        {
            _stopped = true;
            _stopper.Cancel();
        }

        public void Release()
        {
            Registration.Unregister();
            _stopper.Dispose();
        }
    }
}
