using System.Globalization;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Playspace;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Core.Session;

public enum SessionState
{
    Stopped,
    Starting,
    Running,
    Stopping,
}

public enum StartReason
{
    Button,
    Tray,
    Game,
}

public sealed record StartOutcome(bool Ok, string Message);

public sealed record SessionProfile(Profile Profile, IHeadsetModule? Module);

public sealed class SessionController(SessionDeps deps)
{
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);

    public const string StoppedMessage = "Stopped.";

    private volatile bool startsEnded;

    public void EndStarts() => startsEnded = true;

    private const string ServiceName = "monado-service";
    private const string NotActiveNotice = "Monado (this profile) isn't the active OpenXR runtime, so games won't find it.";
    private const int KeptLogs = 10;
    private const int LastLinesShown = 5;

    private const int DllNotFound = unchecked((int)0xC0000135);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan EndLimit = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan LibmonadoLimit = TimeSpan.FromSeconds(10);

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

    internal SessionController(SessionDeps deps, Func<string, bool> isStandIn) : this(deps) => this.isStandIn = isStandIn;

    public event Action? Changed;

    public event Action<IMonadoClient>? MonadoConnected;

    public event Action? StatusChanged;

    public event Action<string>? LogLine;

    public event Action<SessionWarning>? Warning;

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

            lines.AddRange(layer?.Status ?? []);
            lines.AddRange(session?.Status ?? []);

            return lines;
        }
    }

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

    public string? RuntimeNotice =>
        CurrentInstall is { } install && !deps.Runtime.IsActive(install) ? NotActiveNotice : null;

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

    public Task<StartOutcome> StartAsync(StartReason reason, CancellationToken ct) => StartAsync(reason, null, ct);

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

    public void PlaceStandIn() => PlaceStandIn(deps.Profiles.Load().Selected);

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

    private static string OtherMonado(string gameStandIn, MonadoInstall install, Profile? profile)
    {
        var games = Path.GetDirectoryName(Path.GetDirectoryName(gameStandIn)) ?? gameStandIn;
        var ours = Path.GetDirectoryName(install.RuntimeDll) ?? install.Manifest;
        var whose = profile is null ? "Mirador's profile" : $"the profile \"{profile.Name}\"";
        return $"The game uses the Monado in {games}, but {whose} uses the one in {ours}. Pick the profile with the game's Monado in Mirador, or set this profile's Monado as the OpenXR runtime.";
    }

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

            breakOff?.Cancel();
            if (!(await start!.WaitAsync(ct).ConfigureAwait(false)).Ok)
            {
                return;
            }
        }
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        Task? already = null;
        TaskCompletionSource? done = null;
        lock (gate)
        {
            if (state != SessionState.Stopped)
            {
                return;
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

    private Task<StartOutcome> BeginStart(StartReason reason, string? gameStandIn)
    {
        var run = new Run { ForGame = reason == StartReason.Game, GameStandIn = gameStandIn };
        var cancel = new CancellationTokenSource();
        var done = new TaskCompletionSource<StartOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        state = SessionState.Starting;
        current = run;
        startCancel = cancel;
        starting = done.Task;
        var recovery = recovering;
        _ = Task.Run(() => RunStartAsync(run, recovery, done, cancel.Token), CancellationToken.None);
        return done.Task;
    }

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

    private async Task<StartOutcome> StartCoreAsync(Run run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var running = deps.Context.Processes.Snapshot();
        if (FindService(running) is { } other)
        {
            return Fail($"Monado is already running outside Mirador ({ServiceName}, process {Number(other.Id)}). Close it, then press Start.");
        }

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

        if (directMode is not null
            && (await directMode.CheckAsync(deps.Context, ct).ConfigureAwait(false)).FirstOrDefault(c => c.Level == StatusLevel.Error) is { } failed)
        {
            return Fail(failed.Fix is null ? failed.Text : $"{failed.Text} {failed.Fix}");
        }

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

                await Task.WhenAny(process.Exited, Task.Delay(PollInterval, time, wake.Token)).ConfigureAwait(false);
            }
        }
        finally
        {
            wake.Cancel();
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

        _ = CloseLibmonado(run);

        lock (gate)
        {
            if (current == run && state == SessionState.Running)
            {
                _ = BeginStop(run, message, problem: problem);
            }
        }
    }

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

    private async Task EndRunAsync(Run run, bool clean)
    {
        var closing = CloseLibmonado(run);
        try
        {
            await closing.WaitAsync(LibmonadoCloseLimit, deps.Context.Time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        Task? settled;
        lock (gate)
        {
            settled = run.LibmonadoSettled;
        }

        if (settled is { IsCompleted: false })
        {
            try
            {
                await settled.WaitAsync(LibmonadoLimit, deps.Context.Time).ConfigureAwait(false);
            }
            catch (Exception)
            {
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

            await process.DisposeAsync().ConfigureAwait(false);
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
            using var limit = new CancellationTokenSource(EndLimit, deps.Context.Time);
            try
            {
                await session.EndAsync(limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
                keepSessionFile = true;
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

    private void Listen(Run run, IRunningProcess process)
    {
        run.OnListenerFailed = failure => ReportListenerFailure(run, failure);
        run.OnLine = line => OnServiceLine(run, line);
        process.ListenerFailed += run.OnListenerFailed;
        process.Line += run.OnLine;
    }

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

    private const string CompositorFailure = "[comp_main_create_system_compositor] Failed to init compositor";

    private const string CompositorMessage =
        "Monado couldn't start its compositor. Check Diagnostics for the reason in Monado's log, then start again.";

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

    private IProcessLauncher ChooseLauncher(
        MonadoInstall install, Profile profile, IHeadsetModule? module, DirectModeKind directMode, MonadoSettings settings)
    {
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
            }
        }
    }

    private sealed class Reporter(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class LogWriteException(Exception inner) : Exception(inner.Message, inner);

    private sealed class RecoveryWriter(Run run, Action<IReadOnlyDictionary<string, string>> write)
    {
        private readonly object gate = new();
        private bool closed;
        private bool somethingToUndo;

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

    private sealed class SharedMonadoClient(IMonadoClient inner) : IMonadoClient
    {
        private readonly object gate = new();

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

        public MonadoPose GetOriginOffset(uint origin)
        {
            ThrowUnlessOriginOffsets();
            lock (gate)
            {
                ThrowUnlessOriginOffsets();
                return inner.GetOriginOffset(origin);
            }
        }

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

        public void ShowApp(uint id) => Call(inner.OffersApps, () => inner.ShowApp(id));

        public void BlockInputs(uint id, bool blocked) => Call(inner.OffersInputBlocks, () => inner.BlockInputs(id, blocked));

        public MonadoPose GetStageOffset()
        {
            var offset = MonadoPose.Identity;
            Call(inner.OffersStageOffset, () => offset = inner.GetStageOffset());
            return offset;
        }

        public void SetStageOffset(MonadoPose offset) => Call(inner.OffersStageOffset, () => inner.SetStageOffset(offset));

        public void Dispose()
        {
        }

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

        public void EndCalls() => closed = true;

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

    private sealed class Run
    {
        public bool ForGame { get; init; }

        public string? GameStandIn { get; init; }

        public Profile? Profile { get; set; }

        public IHeadsetModule? Module { get; set; }

        public bool UsesLighthouse { get; set; }

        public bool HookStarted { get; set; }

        public string? Progress { get; set; }

        public StatusLine? StandIn { get; set; }

        public ModuleSession? Session { get; set; }

        public ModuleSession? Layer { get; set; }

        public MonadoSettings Settings { get; set; } = new();

        public bool StopsWhenLastAppCloses { get; set; }

        public bool WroteSessionFile { get; set; }

        public TextLog? Log { get; set; }

        public IRunningProcess? Process { get; set; }

        public Task? LibmonadoSettled { get; set; }

        public SharedMonadoClient? Libmonado { get; set; }

        public string? LibmonadoProblem { get; set; }

        public bool LibmonadoClosed { get; set; }

        public Task? LibmonadoClosing { get; set; }

        public Action<string>? OnLine { get; set; }

        public Action<Exception>? OnListenerFailed { get; set; }

        public Action? OnModuleStatus { get; set; }

        public Action<SessionWarning>? OnModuleWarning { get; set; }

        public ClientLogParser Clients { get; } = new();

        public LighthouseLogParser Lighthouse { get; } = new();

        public int LogFailureReported;

        public int ModuleFailureReported;

        public Action<string>? OnModuleStopRequested { get; set; }

        public string? StopReason { get; set; }
    }
}
