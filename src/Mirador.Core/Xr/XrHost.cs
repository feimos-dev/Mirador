using Mirador.Core.Monado;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Session;
using Mirador.Ipc;

namespace Mirador.Core.Xr;

public interface IXrProgram
{
    void Send(string line);
}

public interface IGameExit
{
    bool CanQuit();

    bool Quit();
}

public interface IDashboardRecord
{
    void Hosted(int? processId);

    void Opened(bool open);
}

public sealed record XrHostDeps(
    IProcessLauncher Launcher,
    string ProgramExe,
    IReadOnlyDictionary<string, string> Environment,
    TimeProvider Time,
    Func<bool> HomeEnabled,
    Func<bool> DashboardEnabled,
    Action<string> Log,
    IGameExit? GameExit = null,
    IDashboardRecord? Record = null);

public sealed class XrHost(XrHostDeps deps)
{
    public const int ExitsAllowed = 3;

    public static readonly TimeSpan ExitWindow = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan DevicesInterval = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan QuitLimit = TimeSpan.FromSeconds(1);

    private readonly object gate = new();
    private readonly Slot home = new(XrLink.HomeRole, "The home environment");
    private readonly Slot dashboard = new(XrLink.DashboardRole, "The dashboard");

    private RunState? run;

    public event Action? ProblemChanged;

    public event Action? StopMonadoRequested;

    public string? Problem
    {
        get => home.Problem ?? dashboard.Problem;
    }

    public void Follow(SessionController session)
    {
        session.MonadoConnected += client =>
        {
            if (session.CurrentInstall is { } install)
            {
                MonadoStarted(client, install.Approved?.Manifest ?? install.Manifest);
            }
        };
        session.Changed += () =>
        {
            if (session.State is SessionState.Stopping or SessionState.Stopped)
            {
                _ = MonadoStoppedAsync();
            }
        };
    }

    public void MonadoStarted(IMonadoClient client, string runtimeManifest)
    {
        lock (gate)
        {
            if (run is not null || !client.OffersApps)
            {
                return;
            }

            var environment = new Dictionary<string, string>(deps.Environment, StringComparer.OrdinalIgnoreCase) { ["XR_RUNTIME_JSON"] = runtimeManifest };
            var state = run = new RunState(client, environment);
            home.Reset();
            dashboard.Reset();
            if (!File.Exists(deps.ProgramExe))
            {
                deps.Log($"{deps.ProgramExe} is missing: no home and no dashboard.");
            }
            else
            {
                if (deps.HomeEnabled())
                {
                    StartLocked(state, home);
                }

                if (deps.DashboardEnabled())
                {
                    StartLocked(state, dashboard);
                }
            }

            state.DevicesTimer = deps.Time.CreateTimer(_ => SendDevices(state), null, DevicesInterval, DevicesInterval);
        }

        RaiseProblemChanged();
    }

    public async Task MonadoStoppedAsync()
    {
        RunState? state;
        List<IRunningProcess> ending = [];
        lock (gate)
        {
            state = run;
            run = null;
            if (state is null)
            {
                return;
            }

            state.DevicesTimer?.Dispose();
            state.OpenTimer?.Dispose();
            UnblockLocked(state);
            if (dashboard.Process is not null)
            {
                deps.Record?.Hosted(null);
            }
            foreach (var slot in new[] { home, dashboard })
            {
                slot.RestartTimer?.Dispose();
                slot.RestartTimer = null;
                slot.Link?.Send(XrLink.Quit);
                slot.Link = null;
                if (slot.Process is { } process)
                {
                    ending.Add(process);
                    slot.Process = null;
                }

                slot.Problem = null;
            }
        }

        RaiseProblemChanged();
        await Task.WhenAll(ending.Select(EndAsync)).ConfigureAwait(false);
    }

    public void SwitchesChanged()
    {
        List<IRunningProcess> ending = [];
        lock (gate)
        {
            if (run is not { } state || !File.Exists(deps.ProgramExe))
            {
                return;
            }

            foreach (var (slot, enabled) in new[] { (home, deps.HomeEnabled()), (dashboard, deps.DashboardEnabled()) })
            {
                var runs = slot.Process is not null || slot.RestartTimer is not null || slot.Problem is not null;
                if (enabled && !runs)
                {
                    StartLocked(state, slot);
                }
                else if (!enabled && runs)
                {
                    slot.RestartTimer?.Dispose();
                    slot.RestartTimer = null;
                    slot.Link?.Send(XrLink.Quit);
                    slot.Link = null;
                    slot.Running = false;
                    if (slot.Process is { } process)
                    {
                        ending.Add(process);
                        slot.Process = null;
                    }

                    slot.Reset();
                    GoneLocked(state, slot);
                }
            }
        }

        RaiseProblemChanged();
        foreach (var process in ending)
        {
            _ = EndAsync(process);
        }
    }

    private async Task EndAsync(IRunningProcess process)
    {
        try
        {
            await process.Exited.WaitAsync(QuitLimit, deps.Time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            process.Kill();
        }
        catch (Exception)
        {
        }

        try
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    public bool Started(int? processId, string role)
    {
        lock (gate)
        {
            return processId is { } id && SlotOf(role)?.Process?.Id == id;
        }
    }

    public void Connected(string role, IXrProgram program)
    {
        lock (gate)
        {
            if (run is null || SlotOf(role) is not { } slot)
            {
                program.Send(XrLink.Quit);
                return;
            }

            slot.Link = program;
            slot.Running = false;
        }
    }

    public void Disconnected(string role, IXrProgram program)
    {
        lock (gate)
        {
            if (SlotOf(role) is not { } slot || !ReferenceEquals(slot.Link, program))
            {
                return;
            }

            slot.Link = null;
            slot.Running = false;
            if (run is { } state)
            {
                GoneLocked(state, slot);
            }
        }
    }

    public void Line(string role, string line)
    {
        var stop = false;
        lock (gate)
        {
            if (run is not { } state || SlotOf(role) is not { Link: not null } slot)
            {
                return;
            }

            var (word, value) = XrLink.Split(line);
            try
            {
                if (ReferenceEquals(slot, home))
                {
                    HomeLineLocked(state, word, value);
                }
                else
                {
                    stop = DashboardLineLocked(state, word, value);
                }
            }
            catch (Exception e) when (e is MiradorException or NotSupportedException or ObjectDisposedException)
            {
                deps.Log($"{slot.Name}: \"{word}\" failed: {e.Message}");
            }
        }

        if (stop)
        {
            try
            {
                StopMonadoRequested?.Invoke();
            }
            catch (Exception)
            {
            }
        }
    }

    public static IReadOnlyList<XrDevice> BarDevices(IReadOnlyList<MonadoDevice> devices) =>
    [
        .. DeviceStatus.Controllers(null, devices)
            .Select(c => new XrDevice(
                c.Hand switch
                {
                    Hand.Left => "L",
                    Hand.Right => "R",
                    _ => Tail(c.Serial),
                },
                c.Battery,
                c.Charging))
            .OrderBy(d => d.Label switch
            {
                "L" => 0,
                "R" => 1,
                _ => 2,
            })
            .ThenBy(d => d.Label, StringComparer.Ordinal),
    ];

    private static string Tail(string? serial)
    {
        var clean = new string((serial ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());
        return clean.Length == 0 ? "T" : clean[Math.Max(0, clean.Length - 4)..];
    }

    private Slot? SlotOf(string role) => role switch
    {
        XrLink.HomeRole => home,
        XrLink.DashboardRole => dashboard,
        _ => null,
    };

    private void StartLocked(RunState state, Slot slot)
    {
        slot.RestartTimer?.Dispose();
        slot.RestartTimer = null;
        IRunningProcess process;
        try
        {
            process = deps.Launcher.Start(new ProcessSpec(
                deps.ProgramExe, "--" + slot.Role, Path.GetDirectoryName(deps.ProgramExe) ?? ".", state.Environment));
        }
        catch (Exception e) when (e is MiradorException or ObjectDisposedException)
        {
            deps.Log($"{slot.Name} didn't start: {e.Message}");
            slot.Problem = $"{slot.Name} didn't start. The reason is in mirador.log.";
            return;
        }

        slot.Process = process;
        process.Line += text => deps.Log($"[{slot.Role}] {text}");
        _ = process.Exited.ContinueWith(_ => Exited(state, slot, process), TaskScheduler.Default);
    }

    private void Exited(RunState state, Slot slot, IRunningProcess process)
    {
        var changed = false;
        lock (gate)
        {
            if (!ReferenceEquals(run, state) || !ReferenceEquals(slot.Process, process))
            {
                return;
            }

            slot.Process = null;
            slot.Link = null;
            slot.Running = false;
            GoneLocked(state, slot);

            var now = deps.Time.GetUtcNow();
            slot.Exits.RemoveAll(at => now - at > ExitWindow);
            slot.Exits.Add(now);
            if (slot.Exits.Count >= ExitsAllowed)
            {
                deps.Log($"{slot.Name} stopped {ExitsAllowed} times within a minute: it stays off until Monado starts again.");
                slot.Problem = $"{slot.Name} keeps stopping. It stays off until Monado starts again.";
                changed = true;
            }
            else
            {
                deps.Log($"{slot.Name} stopped; starting it again.");
                slot.RestartTimer = deps.Time.CreateTimer(_ => Restart(state, slot), null, RestartDelay, Timeout.InfiniteTimeSpan);
            }
        }

        _ = process.DisposeAsync();
        if (changed)
        {
            RaiseProblemChanged();
        }
    }

    private void Restart(RunState state, Slot slot)
    {
        lock (gate)
        {
            if (ReferenceEquals(run, state) && slot.Process is null && slot.Problem is null)
            {
                StartLocked(state, slot);
            }
        }

        RaiseProblemChanged();
    }

    private void GoneLocked(RunState state, Slot slot)
    {
        if (ReferenceEquals(slot, home))
        {
            state.HomeShown = false;
            state.OpenTimer?.Dispose();
            state.OpenTimer = null;
        }
        else
        {
            state.Open = false;
            state.Recorded = false;
            UnblockLocked(state);
            deps.Record?.Hosted(null);
        }
    }

    private void HomeLineLocked(RunState state, string word, string value)
    {
        switch (word)
        {
            case XrLink.Running:
                home.Running = true;
                HandBackLocked(state);
                break;
            case XrLink.Shown when XrLink.ParseFlag(value) is { } shown:
                state.HomeShown = shown;
                state.OpenTimer?.Dispose();
                state.OpenTimer = null;
                if (!shown)
                {
                    SetOpenLocked(state, false);
                }
                else
                {
                    state.OpenTimer = deps.Time.CreateTimer(_ => OpenAtHome(state), null, OpenDelay, Timeout.InfiniteTimeSpan);
                }

                break;
        }
    }

    private void HandBackLocked(RunState state)
    {
        var apps = state.Client.Apps();
        if (apps.LastOrDefault(a => a.Name == XrLink.HomeAppName && a.SessionRunning) is not { Shown: true } own)
        {
            return;
        }

        if (apps.LastOrDefault(a => a.SessionRunning && !a.Overlay && a.Id != own.Id && a.Name != XrLink.HomeAppName) is { } game)
        {
            deps.Log($"The home began under {game.Name}: the view goes back to it.");
            state.Client.ShowApp(game.Id);
        }
    }

    private void OpenAtHome(RunState state)
    {
        lock (gate)
        {
            if (ReferenceEquals(run, state) && state.HomeShown)
            {
                try
                {
                    SetOpenLocked(state, true);
                }
                catch (Exception e) when (e is MiradorException or NotSupportedException or ObjectDisposedException)
                {
                    deps.Log($"Opening the dashboard failed: {e.Message}");
                }
            }
        }
    }

    private bool DashboardLineLocked(RunState state, string word, string value)
    {
        switch (word)
        {
            case XrLink.Running:
                dashboard.Running = true;
                state.Recorded = false;
                deps.Record?.Hosted(dashboard.Process?.Id);
                state.Open = false;
                SetOpenLocked(state, state.HomeShown);
                break;
            case XrLink.Toggle:
                SetOpenLocked(state, !state.Open);
                break;
            case XrLink.Recentre when XrLink.ParsePose(value) is { } p:
                var moved = false;
                try
                {
                    if (state.Client.OffersStageOffset)
                    {
                        var origin = DashboardMaths.RecentredOrigin(new MonadoPose(p[0], p[1], p[2], p[3], p[4], p[5], p[6]));
                        state.Client.SetStageOffset(DashboardMaths.Compose(state.Client.GetStageOffset(), origin));
                        moved = true;
                    }

                    if (state.Client.OffersLocalRecentre)
                    {
                        state.Client.RecentreLocalSpaces();
                        moved = true;
                    }
                }
                finally
                {
                    dashboard.Link?.Send(XrLink.Flag(XrLink.Recentred, moved));
                }

                break;
            case XrLink.Exit:
                if (ShownGameLocked(state) is null)
                {
                    return true;
                }

                if (deps.GameExit?.CanQuit() == true && deps.GameExit.Quit())
                {
                    deps.Log("The dashboard's exit button ended the game.");
                }

                break;
        }

        return false;
    }

    private static MonadoApp? ShownGameLocked(RunState state) =>
        state.HomeShown ? null : state.Client.Apps().LastOrDefault(a => a.Shown && !a.Overlay && a.Name != XrLink.HomeAppName);

    private void SetOpenLocked(RunState state, bool open)
    {
        state.Open = open && dashboard.Running;
        if (dashboard.Link is not { } link || !dashboard.Running)
        {
            UnblockLocked(state);
            return;
        }

        var game = state.Open ? ShownGameLocked(state) : null;
        if (game is null || !state.Client.OffersInputBlocks)
        {
            UnblockLocked(state);
        }
        else if (state.Blocked != game.Id)
        {
            UnblockLocked(state);
            state.Client.BlockInputs(game.Id, true);
            state.Blocked = game.Id;
        }

        if (state.Open)
        {
            link.Send(XrLink.FormatExits(game is null ? XrExit.Monado : deps.GameExit?.CanQuit() == true ? XrExit.Game : XrExit.Nothing));
            link.Send(XrLink.FormatDevices(BarDevices(state.Client.Devices())));
        }

        link.Send(XrLink.Flag(XrLink.Open, state.Open));
        if (state.Recorded != state.Open)
        {
            state.Recorded = state.Open;
            deps.Record?.Opened(state.Open);
        }
    }

    private void UnblockLocked(RunState state)
    {
        if (state.Blocked is not { } id)
        {
            return;
        }

        state.Blocked = null;
        try
        {
            state.Client.BlockInputs(id, false);
        }
        catch (Exception e) when (e is MiradorException or NotSupportedException or ObjectDisposedException)
        {
        }
    }

    private void SendDevices(RunState state)
    {
        lock (gate)
        {
            if (!ReferenceEquals(run, state) || !state.Open || dashboard.Link is not { } link)
            {
                return;
            }

            try
            {
                link.Send(XrLink.FormatDevices(BarDevices(state.Client.Devices())));
            }
            catch (Exception e) when (e is MiradorException or ObjectDisposedException)
            {
            }
        }
    }

    private void RaiseProblemChanged()
    {
        try
        {
            ProblemChanged?.Invoke();
        }
        catch (Exception)
        {
        }
    }

    private sealed class RunState(IMonadoClient client, IReadOnlyDictionary<string, string> environment)
    {
        public IMonadoClient Client { get; } = client;

        public IReadOnlyDictionary<string, string> Environment { get; } = environment;

        public bool HomeShown { get; set; }

        public bool Open { get; set; }

        public bool Recorded { get; set; }

        public uint? Blocked { get; set; }

        public ITimer? OpenTimer { get; set; }

        public ITimer? DevicesTimer { get; set; }
    }

    private sealed class Slot(string role, string name)
    {
        public string Role { get; } = role;

        public string Name { get; } = name;

        public IRunningProcess? Process { get; set; }

        public IXrProgram? Link { get; set; }

        public bool Running { get; set; }

        public List<DateTimeOffset> Exits { get; } = [];

        public ITimer? RestartTimer { get; set; }

        public string? Problem { get; set; }

        public void Reset()
        {
            Exits.Clear();
            Problem = null;
            Running = false;
            Link = null;
        }
    }
}
