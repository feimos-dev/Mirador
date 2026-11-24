using Mirador.Core.Monado;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Session;
using Mirador.Ipc;

namespace Mirador.Core.Xr;

/// <summary>A home or dashboard that connected: where Mirador's lines to it go.</summary>
public interface IXrProgram
{
    /// <summary>Queues a line for the program; never blocks or throws.</summary>
    void Send(string line);
}

/// <summary>Ends the game Monado shows, for the dashboard's exit button.</summary>
public interface IGameExit
{
    /// <summary>Whether <see cref="Quit"/> could end the game shown now.</summary>
    bool CanQuit();

    /// <summary>Ends the game shown; false when it couldn't.</summary>
    bool Quit();
}

/// <summary>What an <see cref="XrHost"/> works with.</summary>
/// <param name="Launcher">Starts the two programs. The app owns it; they end with it, and so with Mirador.</param>
/// <param name="ProgramExe">The full path of <c>Mirador.Xr.exe</c>.</param>
/// <param name="Environment">The environment the programs start from: the user's default one.</param>
/// <param name="Time">The clock of every wait.</param>
/// <param name="HomeEnabled">Whether the home is switched on ("Home environment").</param>
/// <param name="DashboardEnabled">Whether the dashboard is switched on ("Dashboard").</param>
/// <param name="Log">Takes a line for Mirador's log.</param>
/// <param name="GameExit">Ends the game shown; null: the exit button only stops Monado.</param>
public sealed record XrHostDeps(
    IProcessLauncher Launcher,
    string ProgramExe,
    IReadOnlyDictionary<string, string> Environment,
    TimeProvider Time,
    Func<bool> HomeEnabled,
    Func<bool> DashboardEnabled,
    Action<string> Log,
    IGameExit? GameExit = null);

/// <summary>
/// Runs the home and the dashboard beside Monado and decides what the dashboard does: started once libmonado is
/// connected, restarted when one exits, ended with Monado. The home tells when it is what Monado shows, which opens the
/// dashboard; the dashboard tells of the system button and its own buttons; Mirador does the rest through libmonado
/// (the view handed back to a game that began first, the game's inputs blocked while the dashboard is open over it, the
/// recentre). Safe from any thread; events come on background threads.
/// </summary>
public sealed class XrHost(XrHostDeps deps)
{
    /// <summary>A program that exits this often within <see cref="ExitWindow"/> stays down until Monado starts again.</summary>
    public const int ExitsAllowed = 3;

    public static readonly TimeSpan ExitWindow = TimeSpan.FromMinutes(1);

    /// <summary>How long after a program's exit it is started again.</summary>
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(1);

    /// <summary>How long the home must stay shown before the dashboard opens: a game taking over at once never sees it.</summary>
    public static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>How often the open dashboard gets the controllers' batteries again.</summary>
    public static readonly TimeSpan DevicesInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long a program has to end by itself when Monado stops.</summary>
    public static readonly TimeSpan QuitLimit = TimeSpan.FromSeconds(1);

    private readonly object gate = new();
    private readonly Slot home = new(XrLink.HomeRole, "The home environment");
    private readonly Slot dashboard = new(XrLink.DashboardRole, "The dashboard");

    // One Monado run: null while Monado is stopped. A late timer or exit of an earlier run sees another object and stops.
    private RunState? run;

    /// <summary>Raised when <see cref="Problem"/> may be different.</summary>
    public event Action? ProblemChanged;

    /// <summary>Raised when the dashboard's exit button asks for Monado to stop.</summary>
    public event Action? StopMonadoRequested;

    /// <summary>Why the home or the dashboard isn't running though it should be; null otherwise.</summary>
    public string? Problem
    {
        get
        {
            lock (gate)
            {
                return home.Problem ?? dashboard.Problem;
            }
        }
    }

    /// <summary>Follows <paramref name="session"/>: the programs start once its libmonado connection is open and end as it stops.</summary>
    public void Follow(SessionController session)
    {
        session.MonadoConnected += client =>
        {
            if (session.CurrentInstall is { } install)
            {
                // The copy that runs: the approved one when there is one.
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

    /// <summary>
    /// Monado is up and libmonado connected: starts the programs that are switched on. Without the app list in this
    /// Monado's libmonado, neither starts: a home couldn't hand the view back to a game that began before it.
    /// </summary>
    /// <param name="client">The run's libmonado connection.</param>
    /// <param name="runtimeManifest">The OpenXR runtime manifest of the Monado that runs, which the programs must use whatever the machine's active runtime is.</param>
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

    /// <summary>
    /// Monado is stopping or has stopped: gives a blocked game its inputs back, then ends the programs, asking first and
    /// waiting at most <see cref="QuitLimit"/>. Never throws.
    /// </summary>
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

    /// <summary>
    /// "Home environment" or "Dashboard" was switched while Monado may be running: a program switched on starts now, one
    /// switched off is ended. Never throws.
    /// </summary>
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

    /// <summary>Waits <see cref="QuitLimit"/> for a program told to quit, then ends it. Never throws.</summary>
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
            // Its exit can't be read: it ends with Mirador at the latest.
        }

        try
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Whether process <paramref name="processId"/> is the program Mirador started as <paramref name="role"/>.</summary>
    public bool Started(int? processId, string role)
    {
        lock (gate)
        {
            return processId is { } id && SlotOf(role)?.Process?.Id == id;
        }
    }

    /// <summary>A program connected as <paramref name="role"/>; its lines go to <see cref="Line"/> and its end to <see cref="Disconnected"/>.</summary>
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

    /// <summary>The program connected as <paramref name="role"/> through <paramref name="program"/> has gone.</summary>
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

    /// <summary>A line from the program connected as <paramref name="role"/>.</summary>
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

    /// <summary>The bar's entries for Monado's <paramref name="devices"/>: the hands first, then the rest by serial.</summary>
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

    /// <summary>What goes with a program that left: the home no longer shows, a dashboard no longer blocks a game.</summary>
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
            UnblockLocked(state);
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
                    // A game took the view: the dashboard closes, and opens over it only on the system button.
                    SetOpenLocked(state, false);
                }
                else
                {
                    state.OpenTimer = deps.Time.CreateTimer(_ => OpenAtHome(state), null, OpenDelay, Timeout.InfiniteTimeSpan);
                }

                break;
        }
    }

    /// <summary>
    /// The home's session began. Monado shows the session that began last, so a game that started Monado and began
    /// before the home has just lost the view: it gets it back.
    /// </summary>
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

    /// <returns>True when Monado should be stopped.</returns>
    private bool DashboardLineLocked(RunState state, string word, string value)
    {
        switch (word)
        {
            case XrLink.Running:
                dashboard.Running = true;
                // A dashboard that starts (again) while the home shows opens at once.
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

    /// <summary>The game Monado shows; null at home, or with nothing shown.</summary>
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

        // The game under an open dashboard gets no buttons, triggers or sticks; its view and hands keep moving.
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
            // The game has gone, or Monado has: nothing is left blocked.
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
                // Monado didn't answer: the bar keeps what it had.
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

        /// <summary>Whether the home is the app Monado shows.</summary>
        public bool HomeShown { get; set; }

        /// <summary>Whether the dashboard is open.</summary>
        public bool Open { get; set; }

        /// <summary>The app whose inputs Mirador blocked, to give them back.</summary>
        public uint? Blocked { get; set; }

        public ITimer? OpenTimer { get; set; }

        public ITimer? DevicesTimer { get; set; }
    }

    private sealed class Slot(string role, string name)
    {
        public string Role { get; } = role;

        /// <summary>The program's name at the start of a sentence.</summary>
        public string Name { get; } = name;

        public IRunningProcess? Process { get; set; }

        public IXrProgram? Link { get; set; }

        /// <summary>Whether the connected program's session has begun.</summary>
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
