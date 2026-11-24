using System.Globalization;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Playspace;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Core.Session;

/// <summary>What the session is doing.</summary>
public enum SessionState
{
    Stopped,
    Starting,
    Running,
    Stopping,
}

/// <summary>Who asked for a Start. It does not change what Start does.</summary>
public enum StartReason
{
    Button,
    Tray,
    Game,
}

/// <summary>How a Start ended.</summary>
/// <param name="Ok">True when Monado is running.</param>
/// <param name="Message">What happened, in plain words; for a refusal or a failure, why and what to do.</param>
public sealed record StartOutcome(bool Ok, string Message);

/// <summary>What a session runs with.</summary>
/// <param name="Profile">The profile, as it was read at the session's Start.</param>
/// <param name="Module">Its headset module; null for a profile without one.</param>
public sealed record SessionProfile(Profile Profile, IHeadsetModule? Module);

/// <summary>
/// Starts and stops Monado's service for the selected profile, with its headset module's steps before and after, and
/// follows Monado's output for the log and the status lines.
/// </summary>
/// <remarks>
/// <para>
/// Everything here may be called from any thread. The events are raised on whichever thread made the change, mostly
/// background ones, and <see cref="LogLine"/> and <see cref="StatusChanged"/> often while Monado's output waits for
/// them: a subscriber must return quickly and hand any UI work to its UI thread asynchronously
/// (<c>DispatcherQueue.TryEnqueue</c>), never wait for that thread. An exception thrown by a subscriber is dropped, so
/// that it can't leave a Start or a Stop half done.
/// </para>
/// </remarks>
public sealed class SessionController(SessionDeps deps)
{
    /// <summary>How long Start waits for Monado to be ready for apps once its service runs.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);

    /// <summary>The end's message when the session was stopped as asked (<see cref="StopAsync"/>).</summary>
    public const string StoppedMessage = "Stopped.";

    // Set as Mirador quits: no Start may begin after its last Stop.
    private volatile bool startsEnded;

    /// <summary>From now on every Start is refused ("Mirador is quitting."); Mirador calls it before its last Stop.</summary>
    public void EndStarts() => startsEnded = true;

    private const string ServiceName = "monado-service";
    private const string NotActiveNotice = "Monado (this profile) isn't the active OpenXR runtime, so games won't find it.";
    private const int KeptLogs = 10;
    private const int LastLinesShown = 5;

    /// <summary>The exit code of a program Windows couldn't find a DLL for (<c>STATUS_DLL_NOT_FOUND</c>).</summary>
    private const int DllNotFound = unchecked((int)0xC0000135);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How long a module's <see cref="ModuleSession.EndAsync"/> may take before Mirador stops waiting for it.</summary>
    private static readonly TimeSpan EndLimit = TimeSpan.FromSeconds(60);

    /// <summary>How long a Start, and then a Stop, waits for libmonado to connect.</summary>
    private static readonly TimeSpan LibmonadoLimit = TimeSpan.FromSeconds(10);

    /// <summary>How long a Stop waits for libmonado's connection to close before it ends Monado anyway.</summary>
    private static readonly TimeSpan LibmonadoCloseLimit = TimeSpan.FromSeconds(5);

    private static readonly IReadOnlyDictionary<string, string> Nothing = new Dictionary<string, string>();

    private readonly SessionDeps deps = deps;
    private readonly SessionFile sessionFile = new(deps.Context.Paths.SessionFile);
    private readonly Func<string, bool> isStandIn = StandIn.IsStandIn;

    private readonly object gate = new();
    private SessionState state = SessionState.Stopped;
    private Run? current;
    private Task<StartOutcome>? starting;
    private CancellationTokenSource? startCancel;
    private Task? stopping;
    private Task recovering = Task.CompletedTask;
    private MonadoInstall? currentInstall;
    private IReadOnlyDictionary<string, string>? lastMiradorEnvironment;
    private TextLog? lastLog;

    /// <summary>For tests: <paramref name="isStandIn"/> stands in for <see cref="StandIn.IsStandIn"/>.</summary>
    internal SessionController(SessionDeps deps, Func<string, bool> isStandIn) : this(deps) => this.isStandIn = isStandIn;

    /// <summary>
    /// Raised when <see cref="State"/>, <see cref="CurrentInstall"/>, <see cref="RuntimeNotice"/>,
    /// <see cref="LastMiradorEnvironment"/>, <see cref="Monado"/> or <see cref="MonadoClientProblem"/> may be different.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Raised once a run's libmonado connection is open, with it (<see cref="Monado"/>). It comes on a pool thread, and may
    /// come while <see cref="State"/> is still <see cref="SessionState.Starting"/> (the connection opens once Monado's pipe
    /// is there, before the Start ends), or later, when libmonado was slow to answer. A subscriber hands UI work to its UI
    /// thread, as for the other events.
    /// </summary>
    public event Action<IMonadoClient>? MonadoConnected;

    /// <summary>Raised when <see cref="Status"/> may be different.</summary>
    public event Action? StatusChanged;

    /// <summary>Raised with each line of Monado's output, in order, while Monado's output waits.</summary>
    public event Action<string>? LogLine;

    /// <summary>Raised when the user should know something during a session: from the headset module, or about Monado's log.</summary>
    public event Action<SessionWarning>? Warning;

    /// <summary>
    /// Raised once a running session has ended, after <see cref="State"/> is <see cref="SessionState.Stopped"/>, with
    /// what happened ("Stopped.", or why Monado stopped on its own or was stopped because something failed) and whether that is a problem. A Start that fails
    /// says so through its outcome instead.
    /// </summary>
    public event Action<SessionEnd>? Ended;

    public SessionState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    /// <summary>
    /// The status lines: <c>monado</c>; while there is a session also <c>apps</c>, <c>tracking</c> (when the profile
    /// uses the Lighthouse receiver), <c>standin</c> (when games can't start Monado from the build), then the direct-mode
    /// layer's and the module's.
    /// </summary>
    public IReadOnlyList<StatusLine> Status
    {
        get
        {
            List<StatusLine> lines;
            ModuleSession? layer = null;
            ModuleSession? session = null;
            lock (gate)
            {
                lines = [MonadoLine()];
                if (current is { } run)
                {
                    lines.Add(AppsLine(run.Clients.ConnectedApps));
                    if (run.UsesLighthouse)
                    {
                        lines.Add(TrackingLine(run.Lighthouse));
                    }

                    if (run.StandIn is { } standIn)
                    {
                        lines.Add(standIn);
                    }

                    layer = run.Layer;
                    session = run.Session;
                }
            }

            // The layer's and the module's own lines are read outside the lock: that is their code.
            lines.AddRange(layer?.Status ?? []);
            lines.AddRange(session?.Status ?? []);

            return lines;
        }
    }

    /// <summary>
    /// The controllers and trackers Monado has found in the current session, in the order found; null unless a session
    /// whose profile uses the Lighthouse receiver is starting or running. A snapshot, safe to read from any thread.
    /// </summary>
    public IReadOnlyList<TrackedDevice>? Devices
    {
        get
        {
            lock (gate)
            {
                return current is { UsesLighthouse: true } run ? run.Lighthouse.Devices : null;
            }
        }
    }

    /// <summary>The last lines of Monado's output in the current or last session (up to 200), oldest first.</summary>
    public IReadOnlyList<string> LogTail
    {
        get
        {
            TextLog? log;
            lock (gate)
            {
                log = lastLog;
            }

            return log?.Tail ?? [];
        }
    }

    /// <summary>
    /// While there is a session: a note when its Monado isn't the active OpenXR runtime, since games then don't find
    /// it; else null. Read fresh from the registry each time, so it goes once the runtime is set.
    /// </summary>
    public string? RuntimeNotice =>
        CurrentInstall is { } install && !deps.Runtime.IsActive(install) ? NotActiveNotice : null;

    /// <summary>
    /// The profile and headset module of the session that is starting (once the Start has read the profile), running or
    /// stopping, until it has ended (after <see cref="ISessionHook.Ended"/>); null otherwise.
    /// </summary>
    public SessionProfile? CurrentProfile
    {
        get
        {
            lock (gate)
            {
                return current is { Profile: { } profile } run ? new SessionProfile(profile, run.Module) : null;
            }
        }
    }

    /// <summary>
    /// The libmonado connection of the running session, opened once Monado is up and closed before it is stopped; null
    /// before that, after it, or without libmonado (<see cref="MonadoClientProblem"/> then says why). Safe to read from
    /// any thread: once the session has closed it, its reads are empty. Only the session closes it; disposing it does
    /// nothing.
    /// </summary>
    public IMonadoClient? Monado
    {
        get
        {
            lock (gate)
            {
                return current?.Libmonado;
            }
        }
    }

    /// <summary>Why the running session has no <see cref="Monado"/> connection, such as <see cref="Libmonado.NotOffered"/>; null otherwise.</summary>
    public string? MonadoClientProblem
    {
        get
        {
            lock (gate)
            {
                return current?.LibmonadoProblem;
            }
        }
    }

    /// <summary>The Monado build of the session that is starting or running; null when stopped.</summary>
    public MonadoInstall? CurrentInstall
    {
        get
        {
            lock (gate)
            {
                return currentInstall;
            }
        }
    }

    /// <summary>
    /// The variables Mirador itself set for Monado at the last Start (the settings, the module's and the profile's),
    /// without the ones inherited from Windows, so with no personal paths; null before the first Start.
    /// </summary>
    public IReadOnlyDictionary<string, string>? LastMiradorEnvironment
    {
        get
        {
            lock (gate)
            {
                return lastMiradorEnvironment;
            }
        }
    }

    /// <summary>
    /// Starts Monado with the selected profile: refuses while another Monado runs (a <c>monado-service</c> that isn't
    /// Mirador's stand-in), or while a program holds the Lighthouse receiver the profile needs; runs the headset module's steps; starts the service and waits
    /// until it is ready for apps (at most <see cref="StartTimeout"/>). Callers during a Start share its outcome; a Start
    /// while Monado runs succeeds at once; a Start while stopping waits for the stop, then starts.
    /// </summary>
    /// <param name="reason">Who asked.</param>
    /// <param name="ct">Stops this caller's waiting only: a Start under way serves every caller, and only
    /// <see cref="StopAsync"/> breaks it off.</param>
    public Task<StartOutcome> StartAsync(StartReason reason, CancellationToken ct) => StartAsync(reason, null, ct);

    /// <inheritdoc cref="StartAsync(StartReason, CancellationToken)"/>
    /// <param name="gameStandIn">For a game's Start: the stand-in its Monado runtime DLL started
    /// (<see cref="StandIn.ProgramOf"/>). Only that Monado may start for it, since the game's DLL then talks to the
    /// service: a Start for another build than the selected profile's is refused before any step, and one that joins or
    /// finds Monado of another build is refused too (that Monado stays).</param>
    public async Task<StartOutcome> StartAsync(StartReason reason, string? gameStandIn, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (startsEnded)
        {
            return new StartOutcome(false, "Mirador is quitting.");
        }

        while (true)
        {
            Task? stopFirst = null;
            Task<StartOutcome>? shared = null;
            var joined = false;
            lock (gate)
            {
                switch (state)
                {
                    case SessionState.Running:
                        joined = true;
                        shared = Task.FromResult(new StartOutcome(true, "Monado is already running."));
                        break;
                    case SessionState.Starting:
                        joined = true;
                        shared = starting;
                        break;
                    case SessionState.Stopping:
                        stopFirst = stopping;
                        break;
                    default:
                        shared = BeginStart(reason, gameStandIn);
                        break;
                }
            }

            if (stopFirst is not null)
            {
                await stopFirst.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            var outcome = await shared!.WaitAsync(ct).ConfigureAwait(false);
            if (joined && outcome.Ok && gameStandIn is not null)
            {
                MonadoInstall? install;
                Profile? profile;
                lock (gate)
                {
                    install = currentInstall;
                    profile = current?.Profile;
                }

                if (install is not null && !StandIn.BelongsTo(gameStandIn, install))
                {
                    return Fail(OtherMonado(gameStandIn, install, profile));
                }
            }

            return outcome;
        }
    }

    /// <summary>
    /// Puts the stand-in below the selected profile's build folder now, as every Start does too, so that a game can
    /// start Monado from that build before Mirador has started it once. Call at launch and when the profiles change.
    /// Does nothing for the bundled Monado (its stand-in is the installer's) or a build that can't be found (its Start
    /// says why).
    /// </summary>
    public void PlaceStandIn() => PlaceStandIn(deps.Profiles.Load().Selected);

    /// <summary>Puts the stand-in below <paramref name="profile"/>'s build folder (Verify's game start needs it there).</summary>
    public void PlaceStandIn(Profile profile)
    {
        try
        {
            if (profile.Monado.Kind is SourceKind.Folder or SourceKind.Branch)
            {
                var install = deps.Locator.Locate(profile);
                StandInCopy.Place(deps.StandInExe, install.StandInDir, install.ServiceExe);
            }
        }
        catch (Exception e) when (e is MiradorException or IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Why a game whose runtime is another Monado than <paramref name="install"/> doesn't get it.</summary>
    private static string OtherMonado(string gameStandIn, MonadoInstall install, Profile? profile)
    {
        var games = Path.GetDirectoryName(Path.GetDirectoryName(gameStandIn)) ?? gameStandIn;
        var ours = Path.GetDirectoryName(install.RuntimeDll) ?? install.Manifest;
        var whose = profile is null ? "Mirador's profile" : $"the profile \"{profile.Name}\"";
        return $"The game uses the Monado in {games}, but {whose} uses the one in {ours}. Pick the profile with the game's Monado in Mirador, or set this profile's Monado as the OpenXR runtime.";
    }

    /// <summary>
    /// Ends Monado and the headset module's session, and deletes <c>session.json</c>. During a Start, breaks it off.
    /// Does nothing when stopped.
    /// </summary>
    /// <param name="ct">Stops this caller's waiting only: a stop, once begun, runs to its end, since a half-done one
    /// would leave the headset's software stopped. The module's part is given at most 60 s; after that the session is
    /// over anyway, <c>session.json</c> stays and the module's steps are undone before Monado next starts.</param>
    /// <param name="restart">
    /// A Start follows at once (Monado settings' Restart): Monado is ended without its teardown, as a Start broken off
    /// is, so that its controllers and trackers don't go into standby.
    /// </param>
    public async Task StopAsync(CancellationToken ct, bool restart = false)
    {
        while (true)
        {
            Task? stop = null;
            Task<StartOutcome>? start = null;
            CancellationTokenSource? breakOff = null;
            lock (gate)
            {
                switch (state)
                {
                    case SessionState.Stopped:
                        return;
                    case SessionState.Starting:
                        start = starting;
                        breakOff = startCancel;
                        break;
                    case SessionState.Stopping:
                        stop = stopping;
                        break;
                    default:
                        stop = BeginStop(current!, StoppedMessage, clean: !restart);
                        break;
                }
            }

            if (stop is not null)
            {
                await stop.WaitAsync(ct).ConfigureAwait(false);
                return;
            }

            // Outside the lock: cancelling runs the Start's cancellation callbacks on this thread.
            breakOff?.Cancel();
            if (!(await start!.WaitAsync(ct).ConfigureAwait(false)).Ok)
            {
                return;
            }

            // Monado came up before the Start saw the request: stop it now.
        }
    }

    /// <summary>
    /// When Mirador went away mid-session (killed or crashed), <c>session.json</c> was left behind: once no Monado
    /// runs (stand-ins don't count), hands its data to the module's <see cref="IHeadsetModule.RecoverAsync"/>, then deletes
    /// it. Meant for Mirador's start; a Start that comes in meanwhile waits for it. A failure becomes a
    /// <see cref="Warning"/> and the file stays. Every Start also undoes a session left behind before its own steps
    /// (and refuses when that fails), so the file is never replaced while it still holds something to undo.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        Task? already = null;
        TaskCompletionSource? done = null;
        lock (gate)
        {
            if (state != SessionState.Stopped)
            {
                return; // session.json belongs to the session that runs now
            }

            if (!recovering.IsCompleted)
            {
                already = recovering;
            }
            else
            {
                done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                recovering = done.Task;
            }
        }

        if (already is not null)
        {
            await already.WaitAsync(ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await RecoverCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            done!.SetResult();
        }
    }

    /// <summary>
    /// Runs work on the headset while Monado is stopped, such as the direct-mode layer's display check, the way recovery
    /// runs: a Start (from any caller) waits until it is done, and what the work saves through its first argument goes
    /// into <c>session.json</c> at once (an empty set deletes the file), so that recovery undoes the work's steps if
    /// Mirador goes away meanwhile. A session left behind earlier is undone first. Works run one after another.
    /// </summary>
    /// <exception cref="MiradorException">Monado isn't stopped, or what an earlier session left behind couldn't be undone.</exception>
    public async Task<T> RunWhileStoppedAsync<T>(
        string moduleId, Func<Action<IReadOnlyDictionary<string, string>>, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        Task before;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (state != SessionState.Stopped)
            {
                throw new MiradorException("Stop Monado first.");
            }

            before = recovering;
            recovering = done.Task;
        }

        try
        {
            await before.WaitAsync(ct).ConfigureAwait(false);
            if (sessionFile.Read() is { } left)
            {
                if (FindService(deps.Context.Processes.Snapshot()) is { } service)
                {
                    throw new MiradorException(
                        $"Monado is running outside Mirador ({ServiceName}, process {service.Id}). Close it, then try again.");
                }

                await UndoLeftSessionAsync(left.ModuleId, left.Data, ct).ConfigureAwait(false);
            }

            var profileId = deps.Profiles.Load().SelectedId;
            void Save(IReadOnlyDictionary<string, string> data)
            {
                if (data.Count == 0)
                {
                    sessionFile.Delete();
                }
                else
                {
                    sessionFile.Write(profileId, moduleId, data);
                }
            }

            return await work(Save, ct).ConfigureAwait(false);
        }
        finally
        {
            done.SetResult();
        }
    }

    /// <summary>Completes when no recovery and no module work (<see cref="RunWhileStoppedAsync"/>) is under way.</summary>
    public Task WhenSettledAsync()
    {
        lock (gate)
        {
            return recovering;
        }
    }

    private async Task RecoverCoreAsync(CancellationToken ct)
    {
        try
        {
            if (sessionFile.Read() is not { } saved)
            {
                return;
            }

            // A service that still runs may be the one this session started: its steps must not be undone under it.
            if (FindService(deps.Context.Processes.Snapshot()) is not null)
            {
                return;
            }

            await UndoLeftSessionAsync(saved.ModuleId, saved.Data, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            RaiseWarning(new SessionWarning("headset", LeftSessionProblem(e)));
        }
    }

    /// <summary>
    /// Hands what a session left in <c>session.json</c> to its module's <see cref="IHeadsetModule.RecoverAsync"/>, then
    /// deletes the file. When the module fails, its exception comes out and the file stays.
    /// </summary>
    private async Task UndoLeftSessionAsync(string? moduleId, IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        if (FindModule(moduleId) is { } module)
        {
            await module.RecoverAsync(deps.Context, data, ct).ConfigureAwait(false);
        }

        sessionFile.Delete();
    }

    private static string LeftSessionProblem(Exception e) =>
        e is MiradorException
            ? e.Message
            : $"Mirador couldn't undo what its last session left behind ({Trimmed(e.Message)}). It tries again the next time Mirador starts.";

    /// <summary>Moves to Starting and sets the Start off. Called under the lock.</summary>
    private Task<StartOutcome> BeginStart(StartReason reason, string? gameStandIn)
    {
        var run = new Run { ForGame = reason == StartReason.Game, GameStandIn = gameStandIn };
        var cancel = new CancellationTokenSource(); // not disposed: it has no timer, and a late Stop may still cancel it
        var done = new TaskCompletionSource<StartOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        state = SessionState.Starting;
        current = run;
        startCancel = cancel;
        starting = done.Task;
        var recovery = recovering;
        _ = Task.Run(() => RunStartAsync(run, recovery, done, cancel.Token), CancellationToken.None);
        return done.Task;
    }

    /// <summary>Moves a running session to Stopping and sets its end off. Called under the lock.</summary>
    private Task BeginStop(Run run, string endedMessage, bool clean = true, bool problem = false)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state = SessionState.Stopping;
        stopping = done.Task;
        _ = Task.Run(() => FinishAsync(run, done, new SessionEnd(endedMessage, problem), clean), CancellationToken.None);
        return done.Task;
    }

    private async Task RunStartAsync(Run run, Task recovery, TaskCompletionSource<StartOutcome> done, CancellationToken ct)
    {
        var outcome = Fail("Mirador couldn't start Monado.");
        try
        {
            RaiseChanged();
            RaiseStatusChanged();
            try
            {
                await recovery.WaitAsync(ct).ConfigureAwait(false);
                outcome = await StartCoreAsync(run, ct).ConfigureAwait(false);
                if (outcome.Ok)
                {
                    // Monado said while it started (the pipe or libmonado wait) that it can't work: that is the Start's
                    // outcome, so no game is told Monado runs, and the end below stops it.
                    lock (gate)
                    {
                        if (run.StopReason is { } reason)
                        {
                            outcome = Fail(reason);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                outcome = Fail("Monado was stopped while it was starting.");
            }
            catch (MiradorException e)
            {
                outcome = Fail(e.Message);
            }
            catch (Exception e)
            {
                outcome = Fail($"Mirador couldn't start Monado: {e.Message}");
            }

            if (!outcome.Ok)
            {
                await EndRunAsync(run, clean: false).ConfigureAwait(false);
            }
        }
        finally
        {
            // Whatever happened above, the state moves on and every caller gets an outcome.
            lock (gate)
            {
                state = outcome.Ok ? SessionState.Running : SessionState.Stopped;
                starting = null;
                startCancel = null;
                if (outcome.Ok && run.StopReason is { } reason)
                {
                    _ = BeginStop(run, reason, problem: true);
                }

                if (!outcome.Ok)
                {
                    current = null;
                    currentInstall = null;
                }
            }

            RaiseChanged();
            RaiseStatusChanged();
            done.SetResult(outcome);
            if (outcome.Ok)
            {
                _ = WatchAsync(run);
            }
        }
    }

    /// <summary>The steps of a Start, in order. A refusal is returned; whatever was set up is ended by the caller.</summary>
    private async Task<StartOutcome> StartCoreAsync(Run run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var running = deps.Context.Processes.Snapshot();
        if (FindService(running) is { } other)
        {
            return Fail($"Monado is already running outside Mirador ({ServiceName}, process {Number(other.Id)}). Close it, then press Start.");
        }

        // A session that wasn't undone (Mirador went away, or its module failed to end) is undone first: the new
        // session's recovery data would otherwise replace what that one needs.
        if (sessionFile.Read() is { } left)
        {
            try
            {
                await UndoLeftSessionAsync(left.ModuleId, left.Data, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                return Fail(LeftSessionProblem(e));
            }
        }

        var profile = deps.Profiles.Load().Selected;
        IHeadsetModule? module = null;
        if (profile.ModuleId is { } moduleId && (module = FindModule(moduleId)) is null)
        {
            return Fail($"This profile uses a headset Mirador doesn't know ({moduleId}).");
        }

        var directModeKind = profile.DirectModeWith(module);
        IDirectMode? directMode = null;
        if (directModeKind != DirectModeKind.None && (directMode = deps.DirectModes?.GetValueOrDefault(directModeKind)) is null)
        {
            return Fail($"This profile uses a direct-mode layer Mirador doesn't know ({directModeKind}).");
        }

        var install = deps.Locator.Locate(profile);
        if (run.GameStandIn is { } gameStandIn && !StandIn.BelongsTo(gameStandIn, install))
        {
            return Fail(OtherMonado(gameStandIn, install, profile));
        }

        // A game that only asks whether a headset is there starts Monado (OpenLamina does):
        // with the headset unplugged, refuse before any step, so the headset's own software is never touched and the game goes on without VR.
        if (run.ForGame && module is not null && !module.IsPresent(deps.Context.Usb.Present()))
        {
            return Fail($"Monado wasn't started: the {module.DisplayName} isn't connected.");
        }

        var usesLighthouse = LighthouseConflicts.UsesLighthouse(profile, module?.UsesSteamVrLighthouse ?? false);
        lock (gate)
        {
            run.Profile = profile;
            run.Module = module;
            run.UsesLighthouse = usesLighthouse;
            currentInstall = install;
        }

        RaiseChanged();
        RaiseStatusChanged();

        if (usesLighthouse && LighthouseConflicts.Find(running) is { Count: > 0 } conflicts)
        {
            return Fail(LighthouseConflicts.Message(conflicts));
        }

        // The layer's refusals come before any step: the module's would stop the headset's own software first.
        if (directMode is not null
            && (await directMode.CheckAsync(deps.Context, ct).ConfigureAwait(false)).FirstOrDefault(c => c.Level == StatusLevel.Error) is { } failed)
        {
            return Fail(failed.Fix is null ? failed.Text : $"{failed.Text} {failed.Fix}");
        }

        // A headset that isn't plugged in skips the hook, whatever started the session; the steps below go on as before.
        if (module is null || module.IsPresent(deps.Context.Usb.Present()))
        {
            run.HookStarted = true;
            CallHook(profile, module, started: true);
        }

        run.Settings = deps.MonadoSettings.Load();
        if (module is not null)
        {
            var recovery = new RecoveryWriter(run, data => sessionFile.Write(profile.Id, module.Id, data));
            ModuleSession session;
            try
            {
                session = await module.BeforeStartAsync(
                    deps.Context, profile, new Reporter(text => ShowProgress(run, text)), recovery.Save, ct).ConfigureAwait(false);
            }
            catch when (recovery.LeftSomethingToUndo)
            {
                // The module refused (or was stopped) without saving that it had undone everything: keep session.json,
                // so that its RecoverAsync, which is safe to repeat, finishes the job at the next launch or Start.
                run.WroteSessionFile = false;
                throw;
            }
            finally
            {
                recovery.Close();
            }

            Attach(run, session, layer: false);
            ShowProgress(run, null);
            ct.ThrowIfCancellationRequested();
        }

        if (directMode is not null)
        {
            var layer = await directMode.BeforeStartAsync(deps.Context, new Reporter(text => ShowProgress(run, text)), ct).ConfigureAwait(false);
            Attach(run, layer, layer: true);
            ShowProgress(run, null);
            ct.ThrowIfCancellationRequested();
        }

        sessionFile.Write(profile.Id, module?.Id, run.Session?.RecoveryData ?? Nothing);
        run.WroteSessionFile = true;

        IReadOnlyList<EnvVar> moduleEnvironment = [.. run.Layer?.Environment ?? [], .. run.Session?.Environment ?? []];

        var controllersOff = deps.ControllersOffAtStop?.Invoke() == true;
        var environment = MonadoEnvironment.Build(deps.InheritedEnvironment, run.Settings, profile, moduleEnvironment, controllersOff);
        var mine = MonadoEnvironment.Build(Nothing, run.Settings, profile, moduleEnvironment, controllersOff);
        run.StopsWhenLastAppCloses = RoomMeasuring.StopsMonado(environment);
        var standIn = install.IsBundled
            ? null
            : StandInCopy.Place(deps.StandInExe, install.StandInDir, install.ServiceExe) ?? ServiceLaunch.Check(Path.GetDirectoryName(install.Manifest)!);

        var logPath = deps.Context.Paths.NewMonadoLogFile(deps.Context.Time.GetLocalNow().DateTime);
        MonadoLogs.Prune(deps.Context.Paths.LogsDir, logPath, KeptLogs);
        run.Log = new TextLog(logPath);
        lock (gate)
        {
            run.StandIn = standIn;
            lastMiradorEnvironment = mine;
            lastLog = run.Log;
        }

        RaiseChanged();
        RaiseStatusChanged();
        ct.ThrowIfCancellationRequested();

        // An approved build runs from its approved copy, the same files as the runtime games load from it, whether the
        // helper runs it as administrator or it runs as the user.
        var serviceExe = install.Approved is { } approved ? Path.Combine(approved.ServerDir, "monado-service.exe") : install.ServiceExe;
        BackUpMonadoFolder();
        var launcher = ChooseLauncher(install, profile, module, directModeKind, run.Settings);
        var process = launcher.Start(new ProcessSpec(serviceExe, "", Path.GetDirectoryName(serviceExe)!, environment));
        run.Process = process;
        Listen(run, process);

        var outcome = await WaitForPipeAsync(run, process, deps.Pipe.NameFor(environment), logPath, Path.GetDirectoryName(serviceExe)!, ct).ConfigureAwait(false);
        if (outcome.Ok && !process.Exited.IsCompleted)
        {
            await OpenLibmonadoAsync(run, install).ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>
    /// Opens libmonado from the build Monado runs from, on a background thread, once its pipe is there. Never again in
    /// the run: when Monado's pipe is missing, libmonado starts Monado itself. Waits at most
    /// <see cref="LibmonadoLimit"/>; an open that ends later is still kept while the run goes on.
    /// </summary>
    private async Task OpenLibmonadoAsync(Run run, MonadoInstall install)
    {
        var binDir = install.Approved is { } approved ? approved.ServerDir : Path.GetDirectoryName(install.RuntimeDll)!;
        var settled = Task.Run(() => deps.OpenLibmonado(binDir))
            .ContinueWith(KeepLibmonado(run), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        lock (gate)
        {
            run.LibmonadoSettled = settled;
        }

        try
        {
            await settled.WaitAsync(LibmonadoLimit, deps.Context.Time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (gate)
            {
                if (run.Libmonado is null && run.LibmonadoProblem is null && !run.LibmonadoClosed)
                {
                    run.LibmonadoProblem = $"Monado didn't answer libmonado within {Number((int)LibmonadoLimit.TotalSeconds)} s.";
                }
            }

            RaiseChanged();
        }
    }

    /// <summary>
    /// Makes a finished open the run's <see cref="Monado"/> (or its <see cref="MonadoClientProblem"/>), and raises
    /// <see cref="MonadoConnected"/>; a connection that arrives after the run closed libmonado is closed at once.
    /// </summary>
    private Action<Task<LibmonadoOpen>> KeepLibmonado(Run run) => opening =>
    {
        var opened = opening.IsCompletedSuccessfully
            ? opening.Result
            : new LibmonadoOpen(null, $"Mirador couldn't open libmonado ({Trimmed(opening.Exception?.GetBaseException().Message ?? "")}).");
        var client = opened.Client is { } connection ? new SharedMonadoClient(connection) : null;
        bool kept;
        lock (gate)
        {
            kept = !run.LibmonadoClosed;
            if (kept)
            {
                run.Libmonado = client;
                run.LibmonadoProblem = client is null ? opened.Problem ?? Libmonado.NotOffered : null;
            }
        }

        if (!kept)
        {
            client?.Close();
            return;
        }

        RaiseChanged();
        if (client is not null)
        {
            Raise<IMonadoClient>(MonadoConnected, client);
        }
    };

    /// <summary>
    /// Takes the run's libmonado connection from readers, then closes it on a background thread; nothing opens one for
    /// the run afterwards. Called before Monado is stopped, and as soon as it is seen to have ended. The task completes
    /// once the connection is closed, which waits for a call under way: one stuck in Monado returns only once Monado
    /// has ended. Called again (Monado ended by itself, then the run's end), it gives the same close.
    /// </summary>
    private Task CloseLibmonado(Run run)
    {
        SharedMonadoClient? client;
        Task closing;
        lock (gate)
        {
            run.LibmonadoClosed = true;
            client = run.Libmonado;
            run.Libmonado = null;
            run.LibmonadoProblem = null;
            if (client is not null)
            {
                client.EndCalls();
                run.LibmonadoClosing = Task.Run(client.Close);
            }

            closing = run.LibmonadoClosing ?? Task.CompletedTask;
        }

        if (client is not null)
        {
            RaiseChanged();
        }

        return closing;
    }

    /// <summary>
    /// Copies Monado's folder before Monado runs, when it changed since the last copy. It never stops the Start: a copy
    /// that can't be taken (a file held, the folder too big) is a warning.
    /// </summary>
    private void BackUpMonadoFolder()
    {
        if (deps.FolderBackups is not { } backups)
        {
            return;
        }

        string? problem;
        try
        {
            backups.SnapshotIfChanged(out problem);
        }
        catch (Exception e)
        {
            problem = $"Mirador couldn't back up Monado's folder ({Trimmed(e.Message)}).";
        }

        if (problem is not null)
        {
            RaiseWarning(new SessionWarning("backup", problem));
        }
    }

    /// <summary>Looks for Monado's pipe every 100 ms until it is there, the service ends, or <see cref="StartTimeout"/> has passed.</summary>
    /// <param name="serviceDir">The folder Monado's service runs from, named when it misses a DLL.</param>
    private async Task<StartOutcome> WaitForPipeAsync(Run run, IRunningProcess process, string pipeName, string logPath, string serviceDir, CancellationToken ct)
    {
        var time = deps.Context.Time;
        using var timeout = new CancellationTokenSource(StartTimeout, time);
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            while (true)
            {
                if (process.Exited.IsCompleted)
                {
                    var code = await process.Exited.ConfigureAwait(false);
                    return Fail(StoppedWhileStarting(code, run.Log!.Tail, serviceDir));
                }

                if (deps.Pipe.Exists(pipeName))
                {
                    return new StartOutcome(true, "Monado is running.");
                }

                if (timeout.IsCancellationRequested)
                {
                    return Fail($"Monado didn't come up within {Number((int)StartTimeout.TotalSeconds)} s. Its log: {logPath}");
                }

                ct.ThrowIfCancellationRequested();

                // A cancelled delay ends the wait too; the checks above then say why.
                await Task.WhenAny(process.Exited, Task.Delay(PollInterval, time, wake.Token)).ConfigureAwait(false);
            }
        }
        finally
        {
            wake.Cancel(); // drops the pending delay's timer
        }
    }

    private static string StoppedWhileStarting(int exitCode, IReadOnlyList<string> tail, string serviceDir)
    {
        var text = exitCode == DllNotFound
            ? $"Monado stopped while starting (exit code {Number(exitCode)}): Windows couldn't find a DLL it needs. It runs from {serviceDir}."
            : $"Monado stopped while starting (exit code {Number(exitCode)}).";
        var last = tail.TakeLast(LastLinesShown).ToArray();
        return last.Length == 0
            ? text
            : text + " Last lines:" + Environment.NewLine + string.Join(Environment.NewLine, last);
    }

    /// <summary>Waits for a running session's service to end by itself, then ends the session and says why.</summary>
    private async Task WatchAsync(Run run)
    {
        string message;
        var problem = true;
        try
        {
            var code = await run.Process!.Exited.ConfigureAwait(false);
            problem = !(run.StopsWhenLastAppCloses && code == 0);
            message = problem
                ? $"Monado stopped by itself (exit code {Number(code)})."
                : "Monado stopped after the last app closed.";
        }
        catch (Exception)
        {
            message = "Monado stopped by itself.";
        }

        _ = CloseLibmonado(run); // Monado has ended, so a call under way returns and the close completes

        lock (gate)
        {
            if (current == run && state == SessionState.Running) // else a Stop got there first
            {
                _ = BeginStop(run, message, problem: problem);
            }
        }
    }

    /// <summary>Ends a running session (state Stopping), then moves to Stopped and raises <see cref="Ended"/>.</summary>
    private async Task FinishAsync(Run run, TaskCompletionSource done, SessionEnd ended, bool clean)
    {
        try
        {
            RaiseChanged();
            RaiseStatusChanged();
            await EndRunAsync(run, clean).ConfigureAwait(false);
        }
        finally
        {
            // Whatever happened above, the state moves on and every caller's wait ends.
            lock (gate)
            {
                state = SessionState.Stopped;
                current = null;
                currentInstall = null;
                stopping = null;
            }

            RaiseChanged();
            RaiseStatusChanged();
            Raise(Ended, ended);
            done.SetResult();
        }
    }

    /// <summary>
    /// Ends what a run set up, in order: its libmonado connection, Monado's service (with <paramref name="clean"/>, asked
    /// to end by itself, so that it tears its devices down; else, or when it doesn't, killed; then its exit awaited), its
    /// log, the module's session, <c>session.json</c>. Never throws: a failure becomes a <see cref="Warning"/>.
    /// </summary>
    private async Task EndRunAsync(Run run, bool clean)
    {
        var closing = CloseLibmonado(run);
        try
        {
            await closing.WaitAsync(LibmonadoCloseLimit, deps.Context.Time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A call stuck in libmonado holds the close: ending Monado below frees it, and the close completes then.
        }

        Task? settled;
        lock (gate)
        {
            settled = run.LibmonadoSettled;
        }

        if (settled is { IsCompleted: false })
        {
            // Monado isn't ended while libmonado still connects to it: a connect after the pipe has gone would start
            // Monado again. The open, once done, sees the run closed and closes its connection itself.
            try
            {
                await settled.WaitAsync(LibmonadoLimit, deps.Context.Time).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Late: Monado is ended anyway.
            }
        }

        if (run.Process is { } process)
        {
            try
            {
                if (!clean || !await process.EndCleanlyAsync(ConsoleExitKey.StopLimit).ConfigureAwait(false))
                {
                    process.Kill();
                }
            }
            catch (Exception e)
            {
                RaiseWarning(new SessionWarning("monado", $"Mirador couldn't end Monado ({Trimmed(e.Message)}). It ends when Mirador quits."));
            }

            await process.DisposeAsync().ConfigureAwait(false); // waits for the exit, at most a few seconds
            process.Line -= run.OnLine;
            process.ListenerFailed -= run.OnListenerFailed;
        }

        run.Log?.Dispose();

        if (run.Layer is { } layer)
        {
            using var limit = new CancellationTokenSource(EndLimit, deps.Context.Time);
            try
            {
                await layer.EndAsync(limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                RaiseWarning(new SessionWarning("direct-mode", e is MiradorException ? e.Message : $"The direct-mode layer didn't end cleanly ({Trimmed(e.Message)})."));
            }

            Detach(run, layer);
        }

        var keepSessionFile = false;
        if (run.Session is { } session)
        {
            // The module gets a token that is cancelled at the limit, and Mirador stops waiting then either way.
            using var limit = new CancellationTokenSource(EndLimit, deps.Context.Time);
            try
            {
                await session.EndAsync(limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
                keepSessionFile = true; // undone through RecoverAsync before Monado next starts
                RaiseWarning(new SessionWarning(
                    "headset",
                    $"{run.Module!.DisplayName} didn't finish stopping within {Number((int)EndLimit.TotalSeconds)} s; Mirador tries again the next time it starts."));
            }
            catch (Exception e)
            {
                keepSessionFile = true;
                RaiseWarning(new SessionWarning(
                    "headset",
                    e is MiradorException
                        ? e.Message
                        : $"Mirador couldn't put things back for the {run.Module!.DisplayName} ({Trimmed(e.Message)}). It tries again the next time Mirador starts."));
            }

            Detach(run, session);
        }

        if (run.WroteSessionFile && !keepSessionFile)
        {
            try
            {
                sessionFile.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                RaiseWarning(new SessionWarning(
                    "session",
                    $"Mirador couldn't delete {deps.Context.Paths.SessionFile} ({Trimmed(e.Message)}). It tries again the next time Mirador starts."));
            }
        }

        if (run.HookStarted)
        {
            CallHook(run.Profile!, run.Module, started: false);
        }
    }

    /// <summary>Tells the hook (<paramref name="started"/>, else ended). It returns at once and never throws; if it does anyway, that is a warning.</summary>
    private void CallHook(Profile profile, IHeadsetModule? module, bool started)
    {
        if (deps.Hook is not { } hook)
        {
            return;
        }

        try
        {
            if (started)
            {
                hook.Started(profile, module);
            }
            else
            {
                hook.Ended(profile, module);
            }
        }
        catch (Exception e)
        {
            RaiseWarning(new SessionWarning("basestations", $"Mirador couldn't switch the base stations: {e.Message}"));
        }
    }

    /// <summary>Follows the module's or the direct-mode layer's session, its status and warnings, until <see cref="EndRunAsync"/>.</summary>
    private void Attach(Run run, ModuleSession session, bool layer)
    {
        run.OnModuleStatus ??= RaiseStatusChanged;
        run.OnModuleWarning ??= RaiseWarning;
        run.OnModuleStopRequested ??= reason => RequestStopAsync(run, reason);
        session.StatusChanged += run.OnModuleStatus;
        session.Warning += run.OnModuleWarning;
        session.StopRequested += run.OnModuleStopRequested;
        lock (gate)
        {
            if (layer)
            {
                run.Layer = session;
            }
            else
            {
                run.Session = session;
            }
        }

        RaiseStatusChanged();
    }

    private static void Detach(Run run, ModuleSession session)
    {
        session.StatusChanged -= run.OnModuleStatus;
        session.Warning -= run.OnModuleWarning;
        session.StopRequested -= run.OnModuleStopRequested;
    }

    private void ShowProgress(Run run, string? text)
    {
        lock (gate)
        {
            run.Progress = text;
        }

        RaiseStatusChanged();
    }

    /// <summary>
    /// Follows the service's output. Failures are listened for first, so that a failure on a line replayed to the
    /// first line handler is heard too.
    /// </summary>
    private void Listen(Run run, IRunningProcess process)
    {
        run.OnListenerFailed = failure => ReportListenerFailure(run, failure);
        run.OnLine = line => OnServiceLine(run, line);
        process.ListenerFailed += run.OnListenerFailed;
        process.Line += run.OnLine;
    }

    /// <summary>
    /// One line of Monado's output: into the log, the parsers, <see cref="LogLine"/> and the module's session. A log that
    /// can't be written or a module that throws doesn't keep the line from the rest; the failures are thrown at the end,
    /// for <see cref="IRunningProcess.ListenerFailed"/>.
    /// </summary>
    private void OnServiceLine(Run run, string line)
    {
        List<Exception>? failures = null;
        try
        {
            run.Log!.Write(line);
        }
        catch (Exception e)
        {
            (failures ??= []).Add(new LogWriteException(e));
        }

        if (line.Contains(CompositorFailure, StringComparison.Ordinal))
        {
            RequestStopAsync(run, CompositorMessage);
        }

        var appsChanged = run.Clients.Feed(line);
        var trackingChanged = run.Lighthouse.Feed(line) && run.UsesLighthouse;
        Raise(LogLine, line);
        if (appsChanged || trackingChanged)
        {
            RaiseStatusChanged();
        }

        foreach (var session in new[] { run.Layer, run.Session })
        {
            try
            {
                session?.OnServiceLine(line);
            }
            catch (Exception e)
            {
                (failures ??= []).Add(e);
            }
        }

        if (failures is not null)
        {
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
        }
    }

    /// <summary>Monado's own line when it can't set up its compositor: it then idles with nothing to show.</summary>
    private const string CompositorFailure = "[comp_main_create_system_compositor] Failed to init compositor";

    private const string CompositorMessage =
        "Monado couldn't start its compositor. Check Diagnostics for the reason in Monado's log, then start again.";

    /// <summary>
    /// Stops a running session for <paramref name="reason"/>, which becomes how it ended. Asynchronous: it is asked for
    /// from the output's reader or a module's thread, and Stop then runs its usual path from another one, so nothing
    /// waits for the thread that asked. A session that is still starting stops as soon as it runs.
    /// </summary>
    private void RequestStopAsync(Run run, string reason) => _ = Task.Run(() =>
    {
        lock (gate)
        {
            if (current != run)
            {
                return;
            }

            if (state == SessionState.Running)
            {
                _ = BeginStop(run, reason, problem: true);
            }
            else if (state == SessionState.Starting)
            {
                run.StopReason ??= reason;
            }
        }
    });

    /// <summary>
    /// Warns once per session about each kind of failure while following the output, with the first one's message: the
    /// log that can't be written (key <c>log</c>), and the module that fails on a line (key <c>module-log</c>).
    /// </summary>
    private void ReportListenerFailure(Run run, Exception failure)
    {
        IEnumerable<Exception> each = failure is AggregateException all ? all.InnerExceptions : [failure];
        foreach (var e in each)
        {
            if (e is LogWriteException)
            {
                if (Interlocked.Exchange(ref run.LogFailureReported, 1) == 0)
                {
                    RaiseWarning(new SessionWarning("log", $"Mirador couldn't write Monado's log: {e.Message}"));
                }
            }
            else if (Interlocked.Exchange(ref run.ModuleFailureReported, 1) == 0)
            {
                RaiseWarning(new SessionWarning(
                    "module-log",
                    $"Mirador's {run.Module?.DisplayName ?? "headset"} support couldn't read Monado's log: {e.Message}"));
            }
        }
    }

    /// <summary>The <c>monado</c> line. Called under the lock.</summary>
    private StatusLine MonadoLine() => state switch
    {
        SessionState.Running => new StatusLine("monado", "Monado", StatusLevel.Ok, $"Running ({current?.Profile?.Name})"),
        SessionState.Starting => new StatusLine("monado", "Monado", StatusLevel.Info, current?.Progress ?? "Starting…"),
        SessionState.Stopping => new StatusLine("monado", "Monado", StatusLevel.Info, "Stopping…"),
        _ => new StatusLine("monado", "Monado", StatusLevel.Info, "Stopped"),
    };

    private static StatusLine AppsLine(IReadOnlyList<string> apps) =>
        apps.Count == 0
            ? new StatusLine("apps", "Apps", StatusLevel.Info, "No apps")
            : new StatusLine("apps", "Apps", StatusLevel.Ok, string.Join(", ", apps));

    private static StatusLine TrackingLine(LighthouseLogParser lighthouse)
    {
        if (lighthouse.HmdSerial is not { } hmd)
        {
            return new StatusLine("tracking", "Tracking", StatusLevel.Info, "Waiting for the headset");
        }

        return new StatusLine("tracking", "Tracking", StatusLevel.Ok, $"HMD {hmd}");
    }

    /// <summary>
    /// The first real Monado among <paramref name="running"/>: a <c>monado-service</c> whose program file isn't Mirador's
    /// stand-in, which runs under the same name while it waits for a Start (one per game). One whose file can't be read
    /// counts as a Monado (when Mirador can't tell, it doesn't start a second service) unless a fresh look shows it has
    /// ended: Windows can't read the file of a process that is gone, and a stand-in that just gave up is no Monado.
    /// </summary>
    private ProcInfo? FindService(IReadOnlyList<ProcInfo> running)
    {
        List<ProcInfo>? unreadable = null;
        foreach (var p in running.Where(p => string.Equals(p.Name, ServiceName, StringComparison.OrdinalIgnoreCase)))
        {
            if (deps.Context.Processes.ImagePathOf(p.Id) is not { } path)
            {
                (unreadable ??= []).Add(p);
            }
            else if (!isStandIn(path))
            {
                return p;
            }
        }

        if (unreadable is null)
        {
            return null;
        }

        var now = deps.Context.Processes.Snapshot();
        return unreadable.FirstOrDefault(p => now.Contains(p));
    }

    /// <summary>
    /// Which launcher starts this Start: the helper's elevated one when the profile has "Run as administrator" on, the
    /// user allowed it, and <see cref="Elevation.ElevatedChoice.RunsAsYouReason"/> (shared with Home's line) finds no
    /// reason against it: this is the installed Mirador, the profile runs Mirador's Monado (or an approved build), it
    /// sets no extra environment variables and its module has administrator settings for the headset; otherwise the job
    /// launcher, which runs Monado as the user. The elevated
    /// launcher itself runs the Start through the job launcher when the helper says SteamVR's Lighthouse driver changed
    /// since the consent (<see cref="HelperProcessLauncher"/>).
    /// Whatever the choice, the rest of the session is the same.
    /// </summary>
    private IProcessLauncher ChooseLauncher(
        MonadoInstall install, Profile profile, IHeadsetModule? module, DirectModeKind directMode, MonadoSettings settings)
    {
        // install is in Mirador's admin-only folder exactly when the profile runs Mirador's (or the bundled) Monado
        // (MonadoLocator). A branch or build folder runs elevated only as its approved copy, which the helper checks.
        var approvedBuild = !install.IsBundled && profile.RunAsAdministrator
            && (install.Approved is not null || deps.ApprovedBuildCurrent?.Invoke(profile) == true);
        var eligible = deps.ElevatedLauncher is not null
            && profile.RunAsAdministrator
            && deps.ElevatedConsented?.Invoke() == true
            && (install.IsBundled || approvedBuild)
            && Elevation.ElevatedChoice.RunsAsYouReason(
                profile, module, deps.ElevatedInstalledCopy?.Invoke() == true, approvedBuild, deps.VariablesApproved?.Invoke(profile) == true) is null;

        if (!eligible)
        {
            return deps.Launcher;
        }

        var start = new ElevatedStart(
            settings.LighthouseWaitMs,
            settings.RenderScalePercent,
            settings.CompositorMinTimeMs,
            module?.ElevatedModuleId ?? "",
            approvedBuild ? profile.Id : "",
            ElevationFix: deps.ElevationFixVerified?.Invoke(profile, install) == true,
            StandbyOnExit: deps.ControllersOffAtStop?.Invoke() == true,
            ProfileId: profile.Id,
            Variables: profile.ExtraEnvironment.Count > 0 ? Elevation.ElevatedChoice.Variables(profile) : null,
            DirectMode: directMode == DirectModeKind.Nvidia ? DirectModeEnvironment.Nvidia : "");
        return deps.ElevatedLauncher!(start);
    }

    private static StartOutcome Fail(string message) => new(false, message);

    private IHeadsetModule? FindModule(string? id) => id is not null && deps.Modules.TryGetValue(id, out var module) ? module : null;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Trimmed(string message) => message.Trim().TrimEnd('.');

    private void RaiseChanged() => Raise(Changed);

    private void RaiseStatusChanged() => Raise(StatusChanged);

    private void RaiseWarning(SessionWarning warning) => Raise(Warning, warning);

    private static void Raise(Action? handlers)
    {
        foreach (var handler in handlers?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception)
            {
                // See the class remarks: a subscriber can't be allowed to stop a Start or a Stop half-way.
            }
        }
    }

    private static void Raise<T>(Action<T>? handlers, T value)
    {
        foreach (var handler in handlers?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<T>)handler)(value);
            }
            catch (Exception)
            {
                // See the class remarks.
            }
        }
    }

    /// <summary>Hands progress on as it is reported (<see cref="Progress{T}"/> would post it to a synchronization context).</summary>
    private sealed class Reporter(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    /// <summary>The log file couldn't take a line.</summary>
    private sealed class LogWriteException(Exception inner) : Exception(inner.Message, inner);

    /// <summary>
    /// Writes the recovery data a module saves while it gets ready (<see cref="IHeadsetModule.BeforeStartAsync"/>), and
    /// nothing once that has ended: a late call must not bring back a <c>session.json</c> the session has deleted.
    /// </summary>
    private sealed class RecoveryWriter(Run run, Action<IReadOnlyDictionary<string, string>> write)
    {
        private readonly object gate = new();
        private bool closed;
        private bool somethingToUndo;

        /// <summary>Whether the last data saved wasn't empty: the module's steps may still need undoing.</summary>
        public bool LeftSomethingToUndo
        {
            get
            {
                lock (gate)
                {
                    return somethingToUndo;
                }
            }
        }

        public void Save(IReadOnlyDictionary<string, string> data)
        {
            lock (gate)
            {
                if (closed)
                {
                    return;
                }

                write(data);
                run.WroteSessionFile = true;
                somethingToUndo = data.Count > 0;
            }
        }

        public void Close()
        {
            lock (gate)
            {
                closed = true;
            }
        }
    }

    /// <summary>
    /// A run's libmonado connection as readers get it. Calls wait for each other; once <see cref="Close"/> has begun,
    /// or when Monado doesn't answer, the reads are empty instead of throwing, without waiting for a call under way.
    /// Only the session closes it: <see cref="Dispose"/> does nothing, since with "exit on disconnect" a closed
    /// connection stops Monado.
    /// </summary>
    private sealed class SharedMonadoClient(IMonadoClient inner) : IMonadoClient
    {
        private readonly object gate = new();

        /// <summary>Set as <see cref="Close"/> begins, outside the lock, so that no new call waits behind a stuck one.</summary>
        private volatile bool closed;

        private bool disposed;

        public bool OffersOriginOffsets => inner.OffersOriginOffsets;

        public IReadOnlyList<MonadoDevice> Devices()
        {
            if (closed)
            {
                return [];
            }

            lock (gate)
            {
                try
                {
                    return closed ? [] : inner.Devices();
                }
                catch (Exception e) when (e is ObjectDisposedException || (e is MiradorException && closed))
                {
                    return [];
                }
            }
        }

        public IReadOnlyList<string> TrackingOrigins()
        {
            if (closed)
            {
                return [];
            }

            lock (gate)
            {
                try
                {
                    return closed || !inner.OffersOriginOffsets ? [] : inner.TrackingOrigins();
                }
                catch (Exception e) when (e is MiradorException or ObjectDisposedException)
                {
                    return [];
                }
            }
        }

        /// <exception cref="MiradorException">Monado has stopped, can't move tracking origins, or didn't answer.</exception>
        public MonadoPose GetOriginOffset(uint origin)
        {
            ThrowUnlessOriginOffsets();
            lock (gate)
            {
                ThrowUnlessOriginOffsets();
                return inner.GetOriginOffset(origin);
            }
        }

        /// <exception cref="MiradorException">Monado has stopped, can't move tracking origins, or didn't answer.</exception>
        public void SetOriginOffset(uint origin, MonadoPose offset)
        {
            ThrowUnlessOriginOffsets();
            lock (gate)
            {
                ThrowUnlessOriginOffsets();
                inner.SetOriginOffset(origin, offset);
            }
        }

        public bool OffersApps => inner.OffersApps;

        public bool OffersInputBlocks => inner.OffersInputBlocks;

        public bool OffersStageOffset => inner.OffersStageOffset;

        public IReadOnlyList<MonadoApp> Apps()
        {
            if (closed)
            {
                return [];
            }

            lock (gate)
            {
                try
                {
                    return closed ? [] : inner.Apps();
                }
                catch (Exception e) when (e is ObjectDisposedException || (e is MiradorException && closed))
                {
                    return [];
                }
            }
        }

        /// <exception cref="MiradorException">Monado has stopped, can't do this, or didn't answer.</exception>
        public void ShowApp(uint id) => Call(inner.OffersApps, () => inner.ShowApp(id));

        /// <exception cref="MiradorException">Monado has stopped, can't do this, or didn't answer.</exception>
        public void BlockInputs(uint id, bool blocked) => Call(inner.OffersInputBlocks, () => inner.BlockInputs(id, blocked));

        /// <exception cref="MiradorException">Monado has stopped, can't do this, or didn't answer.</exception>
        public MonadoPose GetStageOffset()
        {
            var offset = MonadoPose.Identity;
            Call(inner.OffersStageOffset, () => offset = inner.GetStageOffset());
            return offset;
        }

        /// <exception cref="MiradorException">Monado has stopped, can't do this, or didn't answer.</exception>
        public void SetStageOffset(MonadoPose offset) => Call(inner.OffersStageOffset, () => inner.SetStageOffset(offset));

        public void Dispose()
        {
        }

        /// <summary>One call that changes Monado: refused once the connection closes, or when this Monado doesn't offer it.</summary>
        private void Call(bool offered, Action call)
        {
            void ThrowUnlessOpen()
            {
                if (closed)
                {
                    throw new MiradorException("Monado isn't running.");
                }

                if (!offered)
                {
                    throw new MiradorException("This Monado's libmonado can't do that.");
                }
            }

            ThrowUnlessOpen();
            lock (gate)
            {
                ThrowUnlessOpen();
                try
                {
                    call();
                }
                catch (ObjectDisposedException)
                {
                    throw new MiradorException("Monado isn't running.");
                }
            }
        }

        /// <summary>From now on, calls return at once (empty, or with <see cref="MiradorException"/>); one under way goes on.</summary>
        public void EndCalls() => closed = true;

        /// <summary>Closes the connection once a call under way has returned. Never throws.</summary>
        public void Close()
        {
            EndCalls();
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                try
                {
                    inner.Dispose();
                }
                catch (Exception)
                {
                    // The connection goes with Monado anyway.
                }
            }
        }

        private void ThrowUnlessOriginOffsets()
        {
            if (closed)
            {
                throw new MiradorException("Monado isn't running.");
            }

            if (!inner.OffersOriginOffsets)
            {
                throw new MiradorException("This Monado can't move its tracking origins.");
            }
        }
    }

    /// <summary>One Start and, if it succeeds, the session that follows it.</summary>
    private sealed class Run
    {
        /// <summary>A game asked for this Start (through its stand-in), whether or not the stand-in is known.</summary>
        public bool ForGame { get; init; }

        /// <summary>The stand-in of the game that asked for this Start; null when someone else did.</summary>
        public string? GameStandIn { get; init; }

        // Shown in the status: written and read under the controller's lock.
        public Profile? Profile { get; set; }

        public IHeadsetModule? Module { get; set; }

        public bool UsesLighthouse { get; set; }

        /// <summary>The hook was told this session started, so it is told it ended.</summary>
        public bool HookStarted { get; set; }

        public string? Progress { get; set; }

        public StatusLine? StandIn { get; set; }

        public ModuleSession? Session { get; set; }

        /// <summary>The direct-mode layer's session, when the profile uses one.</summary>
        public ModuleSession? Layer { get; set; }

        // Set while starting and read when the session ends, which comes after.
        public MonadoSettings Settings { get; set; } = new();

        /// <summary>Monado's environment makes it exit once the last app disconnects (a variable of the user's or the profile's).</summary>
        public bool StopsWhenLastAppCloses { get; set; }

        public bool WroteSessionFile { get; set; }

        public TextLog? Log { get; set; }

        public IRunningProcess? Process { get; set; }

        // Written and read under the controller's lock.
        /// <summary>Completes once libmonado's open has ended and its connection was kept or closed.</summary>
        public Task? LibmonadoSettled { get; set; }

        public SharedMonadoClient? Libmonado { get; set; }

        public string? LibmonadoProblem { get; set; }

        /// <summary>The run's libmonado connection was closed, or the run ended: no connection is kept for it afterwards.</summary>
        public bool LibmonadoClosed { get; set; }

        /// <summary>The close of the run's libmonado connection, once one began; null before.</summary>
        public Task? LibmonadoClosing { get; set; }

        public Action<string>? OnLine { get; set; }

        public Action<Exception>? OnListenerFailed { get; set; }

        public Action? OnModuleStatus { get; set; }

        public Action<SessionWarning>? OnModuleWarning { get; set; }

        // Safe to read from any thread; fed only from the output's one line at a time.
        public ClientLogParser Clients { get; } = new();

        public LighthouseLogParser Lighthouse { get; } = new();

        // 1 once that kind of failure while following the output has been reported (Interlocked: two output streams).
        public int LogFailureReported;

        public int ModuleFailureReported;

        public Action<string>? OnModuleStopRequested { get; set; }

        /// <summary>Why the run must stop, asked for before it was running; it stops as soon as it is. Under the lock.</summary>
        public string? StopReason { get; set; }
    }
}
