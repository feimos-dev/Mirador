using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Profiles;
using Mirador.Core.Runtime;
using Mirador.Core.Session;
using Mirador.Core.Tests.Monado;
using Mirador.Ipc;

namespace Mirador.Core.Tests.Session;

internal sealed class Steps
{
    private readonly List<string> list = [];

    public void Add(string step)
    {
        lock (list)
        {
            list.Add(step);
        }
    }

    public string[] All
    {
        get
        {
            lock (list)
            {
                return [.. list];
            }
        }
    }
}

internal sealed class FakeProcessList(Steps steps) : IProcessList
{
    public List<ProcInfo> Running { get; } = [];

    public Dictionary<int, string> Images { get; } = [];

    public HashSet<int> EndAfterSnapshot { get; } = [];

    public IReadOnlyList<ProcInfo> Snapshot()
    {
        steps.Add("processes");
        ProcInfo[] now = [.. Running];
        Running.RemoveAll(p => EndAfterSnapshot.Remove(p.Id));
        return now;
    }

    public string? ImagePathOf(int id) => Images.GetValueOrDefault(id);

    public void Add(string name, int id, string? image = null)
    {
        Running.Add(new ProcInfo(name, id));
        if (image is not null)
        {
            Images[id] = image;
        }
    }
}

internal sealed class FakeProcess(Steps steps) : IRunningProcess
{
    private readonly object gate = new();
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<string>? listeners;
    private List<string>? early = [];

    public int Id => 4321;

    public Task<int> Exited => exited.Task;

    public int KilledExitCode { get; set; } = 1;

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    public int Listeners
    {
        get
        {
            lock (gate)
            {
                return listeners?.GetInvocationList().Length ?? 0;
            }
        }
    }

    public event Action<Exception>? ListenerFailed;

    public event Action<string>? Line
    {
        add
        {
            List<Exception> failures = [];
            lock (gate)
            {
                listeners += value;
                if (early is { } missed && value is not null)
                {
                    early = null;
                    foreach (var text in missed)
                    {
                        Deliver(value, text, failures);
                    }
                }
            }

            Report(failures);
        }
        remove
        {
            lock (gate)
            {
                listeners -= value;
            }
        }
    }

    public void Emit(params string[] lines)
    {
        foreach (var text in lines)
        {
            List<Exception> failures = [];
            lock (gate)
            {
                if (listeners is { } handler)
                {
                    Deliver(handler, text, failures);
                }
                else
                {
                    early?.Add(text);
                }
            }

            Report(failures);
        }
    }

    public void Exit(int code) => exited.TrySetResult(code);

    public void Kill()
    {
        steps.Add("kill");
        Killed = true;
        exited.TrySetResult(KilledExitCode);
    }

    public bool EndsCleanly { get; set; }

    public Task<bool> EndCleanlyAsync(TimeSpan limit)
    {
        steps.Add("end cleanly");
        if (EndsCleanly)
        {
            exited.TrySetResult(0);
        }

        return Task.FromResult(EndsCleanly);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        exited.TrySetResult(KilledExitCode);
        return ValueTask.CompletedTask;
    }

    private static void Deliver(Action<string> handler, string text, List<Exception> failures)
    {
        foreach (var each in handler.GetInvocationList())
        {
            try
            {
                ((Action<string>)each)(text);
            }
            catch (Exception e)
            {
                failures.Add(e);
            }
        }
    }

    private void Report(List<Exception> failures)
    {
        foreach (var failure in failures)
        {
            ListenerFailed?.Invoke(failure);
        }
    }
}

internal sealed class FakeLauncher(Steps steps, string sessionFile) : IProcessLauncher
{
    private readonly List<ProcessSpec> specs = [];

    public string[] FirstLines { get; set; } = [];

    public MiradorException? Refusal { get; set; }

    public FakeProcess? Last { get; private set; }

    public bool SessionFileExistedAtLaunch { get; private set; }

    public Action? OnStart { get; set; }

    public int Starts
    {
        get
        {
            lock (specs)
            {
                return specs.Count;
            }
        }
    }

    public ProcessSpec Spec
    {
        get
        {
            lock (specs)
            {
                return Assert.Single(specs);
            }
        }
    }

    public IRunningProcess Start(ProcessSpec spec)
    {
        steps.Add("launch");
        OnStart?.Invoke();
        lock (specs)
        {
            specs.Add(spec);
        }

        if (Refusal is not null)
        {
            throw Refusal;
        }

        SessionFileExistedAtLaunch = File.Exists(sessionFile);
        var process = new FakeProcess(steps);
        process.Emit(FirstLines);
        Last = process;
        return process;
    }
}

internal sealed class FakePipe(Steps steps) : IMonadoPipe
{
    private readonly ConcurrentQueue<string> asked = new();

    public volatile bool Up;

    public TaskCompletionSource FirstPoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyCollection<string> Asked => asked;

    public string NameFor(IReadOnlyDictionary<string, string> env) => new MonadoPipe(folder => folder).NameFor(env);

    public bool Exists(string name)
    {
        steps.Add("pipe");
        asked.Enqueue(name);
        FirstPoll.TrySetResult();
        return Up;
    }
}

internal sealed class FakeLibmonado(Steps steps)
{
    private readonly ConcurrentQueue<string> binDirs = new();

    public LibmonadoOpen? Result { get; set; }

    public FakeMonadoClient? Client { get; private set; }

    public IReadOnlyCollection<string> BinDirs => binDirs;

    public int Opens => binDirs.Count;

    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LibmonadoOpen Open(string binDir)
    {
        steps.Add("libmonado");
        binDirs.Enqueue(binDir);
        Entered.TrySetResult();
        Gate?.Task.Wait(Harness.Limit);
        if (Result is { } result)
        {
            return result;
        }

        Client = new FakeMonadoClient(steps);
        return new LibmonadoOpen(Client, null);
    }
}

internal sealed class FakeMonadoClient(Steps steps) : IMonadoClient
{
    public bool Disposed { get; private set; }

    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource? DevicesGate { get; set; }

    public TaskCompletionSource DevicesEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool OffersOriginOffsets => true;

    public Exception? DevicesFail { get; set; }

    public IReadOnlyList<MonadoDevice> Devices()
    {
        DevicesEntered.TrySetResult();
        DevicesGate?.Task.Wait(Harness.Limit);
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (DevicesFail is { } fail)
        {
            throw fail;
        }

        return [new MonadoDevice(0, "head", "Fake headset", "FAKE-1", 0, false, false, 0)];
    }

    public IReadOnlyList<string> TrackingOrigins()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        return ["Fake origin"];
    }

    public MonadoPose GetOriginOffset(uint origin)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        return MonadoPose.Identity;
    }

    public void SetOriginOffset(uint origin, MonadoPose offset) => ObjectDisposedException.ThrowIf(Disposed, this);

    public void Dispose()
    {
        steps.Add("libmonado closed");
        Disposed = true;
        Closed.TrySetResult();
    }
}

internal sealed class FakeRuntimeReader : IRuntimeRegistryReader
{
    public string? Active { get; set; }

    public string? ActiveRuntime() => Active;
}

internal sealed class FakeHelper : IHelperClient
{
    public Task<HelperReply> SendAsync(HelperCommand command, CancellationToken ct) => Task.FromResult(new HelperReply(true));
}

internal sealed class FakeUsb : IUsbDevices
{
    public HashSet<UsbId> Devices { get; } = [FakeModule.HeadsetId];

    public IReadOnlySet<UsbId> Present() => new HashSet<UsbId>(Devices);
}

internal sealed class FakeModuleSession(
    Steps steps,
    IReadOnlyDictionary<string, string> recoveryData,
    Func<string, Exception>? lineFailure,
    Exception? endFailure,
    bool endHangs) : ModuleSession
{
    private readonly ConcurrentQueue<string> lines = new();
    private int ends;

    public override IReadOnlyList<EnvVar> Environment { get; } = [new("FAKE_HEADSET", "1")];

    public override IReadOnlyDictionary<string, string> RecoveryData => recoveryData;

    public override IReadOnlyList<StatusLine> Status => [new("fake", "Headset", StatusLevel.Ok, "Ready")];

    public IReadOnlyCollection<string> Lines => lines;

    public int Ends => Volatile.Read(ref ends);

    public TaskCompletionSource HasEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override void OnServiceLine(string line)
    {
        lines.Enqueue(line);
        if (lineFailure is not null)
        {
            throw lineFailure(line);
        }
    }

    public override Task EndAsync(CancellationToken ct)
    {
        steps.Add("end");
        Interlocked.Increment(ref ends);
        HasEnded.TrySetResult();
        if (endHangs)
        {
            return new TaskCompletionSource().Task;
        }

        return endFailure is null ? Task.CompletedTask : Task.FromException(endFailure);
    }

    public void Warn(SessionWarning warning) => RaiseWarning(warning);

    public void RequestStop(string reason) => RaiseStopRequested(reason);
}

internal sealed class FakeDirectMode(Steps steps) : IDirectMode
{
    public DirectModeKind Kind => DirectModeKind.Nvidia;

    public string DisplayName => "Fake layer";

    public List<CheckResult> Checks { get; } = [];

    public Exception? Refusal { get; set; }

    public Task<IReadOnlyList<CheckResult>> CheckAsync(ModuleContext ctx, CancellationToken ct)
    {
        steps.Add("layer-checks");
        return Task.FromResult<IReadOnlyList<CheckResult>>([.. Checks]);
    }

    public Task<string?> SetRegisteredAsync(ModuleContext ctx, bool used, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<ModuleSession> BeforeStartAsync(ModuleContext ctx, IProgress<string> progress, CancellationToken ct)
    {
        steps.Add("layer-before-start");
        return Refusal is null ? Task.FromResult<ModuleSession>(new FakeLayerSession()) : Task.FromException<ModuleSession>(Refusal);
    }

    public Task<CheckResult> CheckDisplayAsync(ModuleContext ctx, CancellationToken ct) =>
        Task.FromResult(new CheckResult("Display check", StatusLevel.Ok, "Direct mode works."));

    public Task<string?> FixAsync(ModuleContext ctx, CancellationToken ct) => Task.FromResult<string?>(null);

    public void AddDiagnostics(ModuleContext ctx, IDiagnosticsSink sink)
    {
    }

    private sealed class FakeLayerSession : ModuleSession
    {
        public override IReadOnlyList<EnvVar> Environment { get; } = [new("FAKE_LAYER", "1")];

        public override IReadOnlyDictionary<string, string> RecoveryData { get; } = new Dictionary<string, string>();

        public override IReadOnlyList<StatusLine> Status => [new("display", "Display", StatusLevel.Info, "Waiting for the display")];

        public override Task EndAsync(CancellationToken ct) => Task.CompletedTask;
    }
}

internal sealed class FakeModule(Steps steps) : IHeadsetModule
{
    private int beforeStartCalls;

    public string Id => "fake";

    public string DisplayName => "Fake headset";

    public bool UsesSteamVrLighthouse { get; set; }

    public DirectModeKind DefaultDirectMode { get; set; }

    public string? ElevatedModuleId { get; set; } = "fake";

    public Exception? Refusal { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public string? Progress { get; set; }

    public IReadOnlyDictionary<string, string> RecoveryData { get; set; } = new Dictionary<string, string> { ["wasRunning"] = "true" };

    public IReadOnlyDictionary<string, string>? SavedDuringStart { get; set; }

    public IReadOnlyDictionary<string, string>? SavedBeforeRefusal { get; set; }

    public Action<IReadOnlyDictionary<string, string>>? SaveRecovery { get; private set; }

    public Func<string, Exception>? SessionLineFailure { get; set; }

    public Exception? SessionEndFailure { get; set; }

    public bool SessionEndHangs { get; set; }

    public Exception? RecoverFailure { get; set; }

    public TaskCompletionSource? RecoverGate { get; set; }

    public TaskCompletionSource RecoverEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int BeforeStartCalls => Volatile.Read(ref beforeStartCalls);

    public FakeModuleSession? Session { get; private set; }

    public IReadOnlyDictionary<string, string>? Recovered { get; private set; }

    public static readonly UsbId HeadsetId = new(0x1234, 0x5678);

    public bool IsPresent(IReadOnlySet<UsbId> devices) => devices.Contains(HeadsetId);

    public Task<IReadOnlyList<CheckResult>> CheckAsync(ModuleContext ctx, Profile profile, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CheckResult>>([]);

    public async Task<ModuleSession> BeforeStartAsync(
        ModuleContext ctx, Profile profile, IProgress<string> progress, Action<IReadOnlyDictionary<string, string>> saveRecovery, CancellationToken ct)
    {
        steps.Add("before-start");
        Interlocked.Increment(ref beforeStartCalls);
        SaveRecovery = saveRecovery;
        if (SavedDuringStart is not null)
        {
            saveRecovery(SavedDuringStart);
        }

        if (Progress is not null)
        {
            progress.Report(Progress);
        }

        Entered.TrySetResult();
        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(ct);
        }

        if (Refusal is not null)
        {
            if (SavedBeforeRefusal is not null)
            {
                saveRecovery(SavedBeforeRefusal);
            }

            throw Refusal;
        }

        return Session = new FakeModuleSession(steps, RecoveryData, SessionLineFailure, SessionEndFailure, SessionEndHangs);
    }

    public async Task RecoverAsync(ModuleContext ctx, IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        steps.Add("recover");
        RecoverEntered.TrySetResult();
        if (RecoverGate is not null)
        {
            await RecoverGate.Task.WaitAsync(ct);
        }

        if (RecoverFailure is not null)
        {
            throw RecoverFailure;
        }

        Recovered = data;
    }

    public void AddDiagnostics(ModuleContext ctx, Profile profile, IDiagnosticsSink sink)
    {
    }
}

internal sealed class FakeHook : ISessionHook
{
    private readonly List<string> calls = [];

    public Exception? Failure { get; set; }

    public Action? OnStarted { get; set; }

    public Action? OnEnded { get; set; }

    public string[] Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public void Started(Profile profile, IHeadsetModule? module)
    {
        OnStarted?.Invoke();
        Add($"started:{profile.Id}:{module?.Id}");
    }

    public void Ended(Profile profile, IHeadsetModule? module)
    {
        OnEnded?.Invoke();
        Add($"ended:{profile.Id}:{module?.Id}");
    }

    private void Add(string call)
    {
        lock (calls)
        {
            calls.Add(call);
        }

        if (Failure is not null)
        {
            throw Failure;
        }
    }
}

internal sealed class Harness : IAsyncDisposable
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly TempDir dir = new();

    public Harness(
        ISessionHook? hook = null, bool? installedCopy = null, Func<Profile, MonadoInstall, Mirador.Ipc.ApprovedBuild?>? approvedCopy = null,
        Func<Profile, MonadoInstall, bool>? elevationFix = null, bool controllersOffAtStop = false, long? folderBackupLimit = null,
        Func<Profile, bool>? variablesApproved = null)
    {
        Root = Path.Combine(dir.Path, "Zoë's data");
        Paths = new AppPaths(Path.Combine(Root, "Mirador"));

        var bundledRoot = Path.Combine(Root, "Mirador App", "monado");
        const string version = "monado-2026.11.10-1a2b3c4d5e";
        var dll = Touch(bundledRoot, version, "bin", "openxr_monado.dll");
        BundledManifest = Path.Combine(bundledRoot, version, "openxr_monado.json");
        File.WriteAllText(BundledManifest, TestManifest.Json(dll));
        BundledServer = Touch(bundledRoot, version, "bin", "monado-service.exe");
        MiradorMonado.Write(bundledRoot, new MiradorMonadoVersions(version, null));

        StandInExe = Path.Combine(Root, "Mirador App", "standin", "mirador-standin.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(StandInExe)!);
        File.WriteAllBytes(StandInExe, [0x4D, 0x5A, 1, 2, 3]);

        Timers = new TimerWatch(Time);
        Processes = new FakeProcessList(Steps);
        Module = new FakeModule(Steps);
        DirectMode = new FakeDirectMode(Steps);
        Launcher = new FakeLauncher(Steps, Paths.SessionFile);
        Pipe = new FakePipe(Steps);
        Libmonado = new FakeLibmonado(Steps);
        Runtime = new FakeRuntimeReader { Active = BundledManifest };
        Profiles = new ProfileStore(Paths.ProfilesFile);
        Settings = new MonadoSettingsStore(Paths.MonadoSettingsFile);
        Usb = new FakeUsb();
        UseProfile(DreamAir);

        Controller = new SessionController(
            new SessionDeps(
                Profiles,
                Settings,
                new Dictionary<string, IHeadsetModule> { [Module.Id] = Module },
                new ModuleContext(Paths, new FakeHelper(), Processes, Usb, Timers),
                new MonadoLocator(bundledRoot, approvedCopy: approvedCopy),
                Launcher,
                Pipe,
                new OpenXrRuntime(Runtime),
                StandInExe,
                Inherited,
                hook,
                ElevatedConsented: installedCopy is null ? null : () => true,
                ElevatedLauncher: installedCopy is null ? null : start =>
                {
                    ElevatedStarts.Enqueue(start);
                    return Launcher;
                },
                ElevatedInstalledCopy: installedCopy is { } installed ? () => installed : null,
                DirectModes: new Dictionary<DirectModeKind, IDirectMode> { [DirectMode.Kind] = DirectMode },
                ElevationFixVerified: elevationFix,
                VariablesApproved: variablesApproved,
                ControllersOffAtStop: () => controllersOffAtStop,
                OpenLibmonado: Libmonado.Open,
                FolderBackups: folderBackupLimit is { } limit
                    ? new MonadoFolderBackups(MonadoFolder, Paths.BackupsDir, Time, maxBytes: limit)
                    : null),
            isStandIn: path => path.StartsWith(StandInImages, StringComparison.OrdinalIgnoreCase));

        Controller.Changed += () => Changes.Enqueue(Controller.State);
        Controller.Warning += w => Warnings.Enqueue(w);
        Controller.LogLine += l => LogLines.Enqueue(l);
        Controller.Ended += e =>
        {
            EndedMessages.Enqueue(e.Message);
            EndedProblems.Enqueue(e.Problem);
            HasEnded.TrySetResult(e.Message);
        };
    }

    public static Profile DreamAir { get; } =
        new("p1", "Dream Air", "fake", BuildSource.Mirador, [], new Dictionary<string, string>());

    public const string StandInImages = @"C:\Games\stand-ins\";

    public const string OtherMonado = @"C:\Users\Zoë\monado\build\src\xrt\targets\service\monado-service.exe";

    public static IReadOnlyDictionary<string, string> Inherited { get; } = new Dictionary<string, string>
    {
        ["USERPROFILE"] = @"C:\Users\Zoë",
        ["TEMP"] = @"C:\Users\Zoë\AppData\Local\Temp",
    };

    public string Root { get; }

    public AppPaths Paths { get; }

    public string BundledManifest { get; }

    public string BundledServer { get; }

    public string MonadoFolder => Path.Combine(Root, "monado");

    public string RuntimeDir => Path.GetDirectoryName(BundledServer)!;

    public string StandInExe { get; }


    public Steps Steps { get; } = new();

    public FakeTimeProvider Time { get; } = new();

    public TimerWatch Timers { get; }

    public FakeProcessList Processes { get; }

    public FakeModule Module { get; }

    public FakeDirectMode DirectMode { get; }

    public FakeLauncher Launcher { get; }

    public System.Collections.Concurrent.ConcurrentQueue<ElevatedStart> ElevatedStarts { get; } = new();

    public FakeUsb Usb { get; }

    public FakePipe Pipe { get; }

    public FakeLibmonado Libmonado { get; }

    public FakeRuntimeReader Runtime { get; }

    public ProfileStore Profiles { get; }

    public MonadoSettingsStore Settings { get; }

    public SessionController Controller { get; }

    public ConcurrentQueue<SessionState> Changes { get; } = new();

    public ConcurrentQueue<SessionWarning> Warnings { get; } = new();

    public ConcurrentQueue<string> LogLines { get; } = new();

    public ConcurrentQueue<string> EndedMessages { get; } = new();

    public ConcurrentQueue<bool> EndedProblems { get; } = new();

    public TaskCompletionSource<string> HasEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string MonadoLog => Paths.NewMonadoLogFile(Time.GetLocalNow().DateTime);

    public void UseProfile(Profile p) => Profiles.Save(new ProfileSet([p], p.Id));

    public static string Touch(params string[] parts)
    {
        var path = Path.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    public static string MakeBuild(string folder)
    {
        var dll = Touch(folder, "src", "xrt", "targets", "openxr", "openxr_monado.dll");
        File.WriteAllText(Path.Combine(folder, "openxr_monado-dev.json"), TestManifest.Json(dll));
        Touch(folder, "src", "xrt", "targets", "service", "monado-service.exe");
        return Path.GetDirectoryName(dll)!;
    }

    public async Task<StartOutcome> StartRunningAsync()
    {
        Pipe.Up = true;
        var outcome = await Controller.StartAsync(StartReason.Button, CancellationToken.None).WaitAsync(Limit);
        Assert.True(outcome.Ok, outcome.Message);
        return outcome;
    }

    public async ValueTask DisposeAsync()
    {
        await Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);
        dir.Dispose();
    }
}
