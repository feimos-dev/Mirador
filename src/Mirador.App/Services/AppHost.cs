using System.Security.Principal;
using Microsoft.Win32;
using Mirador.Core;
using Mirador.Core.BaseStations;
using Mirador.Core.Build;
using Mirador.Core.Elevation;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Native;
using Mirador.Core.OpenVr;
using Mirador.Core.Playspace;
using Mirador.Core.Profiles;
using Mirador.Core.Runtime;
using Mirador.Core.Session;
using Mirador.Core.Xr;
using Mirador.DirectMode;
using Mirador.Pimax;
using Mirador.Ipc;

namespace Mirador.App.Services;

public sealed class AppHost : IAsyncDisposable
{
    private static readonly TimeSpan StationsLimit = TimeSpan.FromSeconds(15);

    private static readonly HttpClient ToolsHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly JobProcessLauncher launcher;
    private readonly List<string> notices = [];
    private readonly WinRtStationRadio radio;
    private readonly StationStore stationStore;
    private readonly XrHostPipe xrPipe;
    private AppPipeServer? pipe;

    private volatile bool steamVrChanged;

    private volatile string? helperInstallDir;

    private readonly FileHashCache buildHashes = new(Ipc.ApprovedBuilds.HashOf);

    private AppHost(string appDir, AppPaths paths)
    {
        AppDir = appDir;
        Paths = paths;
        Profiles = new ProfileStore(paths.ProfilesFile);
        MonadoSettings = new MonadoSettingsStore(paths.MonadoSettingsFile);
        Settings = new AppSettingsStore(paths.SettingsFile);
        Helper = new HelperClient();
        Runtime = new OpenXrRuntime(new RegistryRuntimeReader());
        Locator = new MonadoLocator(Path.Combine(appDir, "monado"), paths.BuildsDir, ApprovedCopyOf);
        Tools = BuildTools.For(appDir, paths);
        Builds = new Builds(new MonadoBuilder(
            new CommandRunner(), Locator, Tools,
            new BuildChecks(new CommandRunner(), Environment.SystemDirectory, TimeProvider.System), TimeProvider.System,
            (line, ct) => BuildToolsSetup!.PrepareAsync(line, ct)),
            NewBuildApproval);
        BuildToolsSetup = new BuildToolsSetup(
            new CommandRunner(), ToolsHttp, new UacLauncher(), paths.ToolsDir,
            BuildToolsCheck.ReadPin(Path.Combine(appDir, "tools", "vcpkg-commit.txt")) ?? "",
            VsWhere.DefaultPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer"),
            WinTrustSigner.SubjectOf, ToolPins.All);
        Usb = new WmiUsbDevices();
        Processes = new SystemProcessList();
        Context = new ModuleContext(paths, Helper, Processes, Usb, TimeProvider.System);
        DreamAirState = new DreamAirStateStore(paths.DreamAirFile);
        FolderBackups = new MonadoFolderBackups(
            MonadoFolderBackups.DefaultMonadoDir, paths.BackupsDir, TimeProvider.System, () => Session!.State != SessionState.Stopped);
        SteamVrSettings = SteamVrSettings.DefaultPath() is { } vrsettings
            ? new SteamVrSettings(vrsettings, Path.Combine(paths.BackupsDir, "steamvr"), TimeProvider.System)
            : null;

        var dreamAir = new PimaxModule(new PimaxDeps(
            new WindowsRegistryReader(),
            new GeometryUploader(ConsoleProcess.Start, Usb, Processes, paths, TimeProvider.System),
            DreamAirState,
            DreamAirChecks.FindSteamVr,
            state => BlobChoice.Resolve(state, null).Variant,
            () => Usb.Present()));
        Modules = new Dictionary<string, IHeadsetModule> { [dreamAir.Id] = dreamAir };
        var nvidia = new NvidiaDirectMode(new NvidiaDirectModeDeps(
            Path.Combine(appDir, "layer"),
            ConsoleProcess.Start,
            new DirectModeStateStore(paths.DirectModeFile),
            NvidiaDriver.CurrentWindowsVersion,
            HeadsetMonitor.List,
            () => Session!.State != SessionState.Stopped));
        DirectModes = new Dictionary<DirectModeKind, IDirectMode> { [nvidia.Kind] = nvidia };
        OpenComposite = new OpenCompositeInstall(OpenCompositeInstall.DefaultFolder);
        OpenVr = new OpenVrGames(new OpenVrPaths(OpenVrPaths.DefaultFile, OpenComposite.Folder), OpenComposite);
        OpenVrWatch = new FolderWatch(
            [Path.GetDirectoryName(OpenVrPaths.DefaultFile)!, OpenComposite.Folder], TimeSpan.FromMilliseconds(500), text => AppLog.Write(text));

        radio = new WinRtStationRadio(text => AppLog.Write(text));
        stationStore = new StationStore(paths.BaseStationsFile);
        Stations = new StationManager(radio, stationStore, StationSettings, TimeProvider.System, text => AppLog.Write(text));

        var self = Environment.ProcessPath;
        launcher = new JobProcessLauncher(self is null ? null : pid => ConsoleExitKey.RunSender(self, pid));
        ElevatedConsent = new ElevatedConsent(new RegistryConsentValues());
        var elevatedLogPath = ElevatedLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Mirador", "monado", CurrentSid() + ".log");
        Pipe = new MonadoPipe();
        InheritedEnvironment = UserEnvironment.Default(e => AppLog.Write("Windows didn't give the user's environment; Monado gets Mirador's own", e));
        Verify = new VerifyBuild(new CommandRunner(), Path.Combine(appDir, "tools", "xr-check.exe"), TimeProvider.System);
        Session = new SessionController(new SessionDeps(
            Profiles,
            MonadoSettings,
            Modules,
            Context,
            Locator,
            launcher,
            Pipe,
            Runtime,
            StandInExe,
            InheritedEnvironment,
            Stations,
            ElevatedConsented: ElevatedMonadoAllowed,
            ApprovedBuildCurrent: ApprovedBuildCurrent,
            ElevatedLauncher: start => new HelperProcessLauncher(Helper, start, elevatedLogPath, launcher)
            {
                SteamVrChecked = OnSteamVrChecked,
            },
            ElevatedInstalledCopy: () => IsInstalledCopy(askHelper: true),
            DirectModes: DirectModes,
            ElevationFixVerified: ElevationFixVerified,
            ControllersOffAtStop: () => StationSettings().ControllersOffAtStop,
            FolderBackups: FolderBackups,
            VariablesApproved: VariablesApproved));

        RoomSetups = new RoomSetupStore(paths.RoomSetupFile);
        RoomMeasuring = new RoomMeasuring(new CommandRunner(), Path.Combine(appDir, "tools", "xr-check.exe"));
        Session.MonadoConnected += client => _ = Task.Run(() => ApplyRoomSetup(client));

        var openLamina = new OpenLaminaDashboard(new OpenLaminaStore(OpenLaminaStore.DefaultDirectory), new ProcessProbe(), text => AppLog.Write(text));
        Xr = new XrHost(new XrHostDeps(
            launcher,
            Path.Combine(appDir, "Mirador.Xr.exe"),
            InheritedEnvironment,
            TimeProvider.System,
            () => StationSettings().HomeEnvironment,
            () => StationSettings().Dashboard,
            text => AppLog.Write(text),
            GameExit: openLamina,
            Record: openLamina));
        Xr.Follow(Session);
        Xr.StopMonadoRequested += () => _ = Task.Run(() => Session.StopAsync(CancellationToken.None));
        xrPipe = new XrHostPipe(Xr, XrLink.CurrentName(), text => AppLog.Write(text));
        xrPipe.Start();
    }

    public XrHost Xr { get; }

    public RoomSetupStore RoomSetups { get; }

    public RoomMeasuring RoomMeasuring { get; }

    public (string? Missing, string? Changed) RoomSetupWarnings()
    {
        try
        {
            var profile = Profiles.Load().Selected;
            var moduleUsesLighthouse = ModuleUsesLighthouse(profile);
            var usesSteamVrLh = LighthouseConflicts.UsesLighthouse(profile, moduleUsesLighthouse);
            if (!profile.UsesBaseStations(moduleUsesLighthouse) && !usesSteamVrLh)
            {
                return (null, null);
            }

            var stations = KnownStations();
            var setups = RoomSetups.All();
            SteamVrNow now;
            try
            {
                now = SteamVrNow.Read(SteamDir(), stations);
            }
            catch (MiradorException)
            {
                return (RoomSetupWarning.FromSteam(SteamDir(), stations, setups, usesSteamVrLh), null);
            }

            var missing = RoomSetupWarning.For(now.Pick, now.RoomSetups, RoomSetupWarning.HasSetup(setups, now.Pick.Id, usesSteamVrLh), usesSteamVrLh);
            var changed = RoomSetupWarning.BaseChanged(setups, now.Pick.Id, now.Base, usesSteamVrLh) ? RoomSetupWarning.SteamVrChanged : null;
            return (missing, changed);
        }
        catch (Exception e)
        {
            AppLog.Write("Couldn't check the room setup", e);
            return (null, null);
        }
    }

    public bool RunUsesBaseStations()
    {
        var profile = Session.CurrentProfile?.Profile ?? Profiles.Load().Selected;
        var moduleUsesLighthouse = ModuleUsesLighthouse(profile);
        return profile.UsesBaseStations(moduleUsesLighthouse) || LighthouseConflicts.UsesLighthouse(profile, moduleUsesLighthouse);
    }

    private bool ModuleUsesLighthouse(Profile profile) =>
        profile.ModuleId is { } id && Modules.TryGetValue(id, out var module) && module.UsesSteamVrLighthouse;

    public IReadOnlyList<KnownStation> KnownStations() => [.. Stations.Stations.Select(v => v.Station)];

    public event Action<string?>? RoomSetupApplied;

    private void ApplyRoomSetup(IMonadoClient client)
    {
        string? problem = null;
        try
        {
            var applied = RunUsesBaseStations()
                ? RoomSetupApplier.ApplyWithSteamVr(client, RoomSetups, SteamDir(), KnownStations())
                : RoomSetupApplier.Apply(client, RoomSetups, universe: null);
            if (applied > 0)
            {
                AppLog.Write($"Room setup set for {applied} tracking origin{(applied == 1 ? "" : "s")}.");
            }
        }
        catch (Exception e)
        {
            if (!ReferenceEquals(Session.Monado, client))
            {
                return;
            }

            AppLog.Write("Couldn't apply the room setup", e);
            problem = $"Couldn't apply the room setup: {e.Message}";
        }

        if (!ReferenceEquals(Session.Monado, client))
        {
            return;
        }

        try
        {
            RoomSetupApplied?.Invoke(problem);
        }
        catch (Exception e)
        {
            AppLog.Write("Couldn't show how the room setup went", e);
        }
    }

    public static string? SteamDir()
    {
        try
        {
            return Mirador.Ipc.SteamVr.SteamPathFromRegistry() is { Length: > 0 } steam ? Path.GetFullPath(steam) : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }

    public const string SteamVrChangedReason =
        "Runs as you: SteamVR's Lighthouse driver changed since you allowed Monado as administrator.";

    public const string NotAllowedReason =
        "Runs as you: Windows hasn't been asked yet for Monado to run as administrator.";

    public const string ApprovalCancelled = "You cancelled the prompt; the build runs as you.";

    public bool FirstRun { get; private set; }

    private readonly TaskCompletionSource<bool> firstRunAnswered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<bool> FirstRunAnswered => FirstRun ? firstRunAnswered.Task : Task.FromResult(true);

    public void AnswerFirstRun(bool bundled) => firstRunAnswered.TrySetResult(bundled);

    public string AppDir { get; }

    public AppPaths Paths { get; }

    public ProfileStore Profiles { get; }

    public MonadoSettingsStore MonadoSettings { get; }

    public AppSettingsStore Settings { get; }

    public IHelperClient Helper { get; }

    public OpenXrRuntime Runtime { get; }

    public MonadoLocator Locator { get; }

    public Builds Builds { get; }

    public BuildToolsSetup BuildToolsSetup { get; }

    public VerifyBuild Verify { get; }

    public string ElevatedLogPath { get; }

    public IMonadoPipe Pipe { get; }

    public IReadOnlyDictionary<string, string> InheritedEnvironment { get; }

    public IProcessLauncher Launcher => launcher;

    public string VerifyResultFile(string profileId) => Path.Combine(Paths.ChecksDir, $"verify-{profileId}.json");

    private StandInWait? standInExpected;

    public void ExpectStandIn(StandInWait wait)
    {
        wait.Ended = () => Interlocked.CompareExchange(ref standInExpected, null, wait);
        Volatile.Write(ref standInExpected, wait);
    }

    public bool Verifying { get; set; }

    public async Task<string?> TryAnswerStandInAsync(string? standIn, CancellationToken ct)
    {
        if (Volatile.Read(ref standInExpected) is not { } expected || standIn is null || !StandIn.BelongsTo(standIn, expected.Install))
        {
            return null;
        }

        try
        {
            await expected.StartAsync(ct);
            return AppPipe.Ok;
        }
        catch (MiradorException e)
        {
            AppLog.Write($"Verify couldn't start Monado for the stand-in: {e.Message}");
            return AppPipe.FailPrefix + e.Message;
        }
    }

    public IUsbDevices Usb { get; }

    public IProcessList Processes { get; }

    public ModuleContext Context { get; }

    public DreamAirStateStore DreamAirState { get; }

    public MonadoFolderBackups FolderBackups { get; }

    public BuildTools Tools { get; }

    public SteamVrSettings? SteamVrSettings { get; }

    public string? SteamVrButtonStartWarning()
    {
        try
        {
            return Settings.Load().KeepSteamVrFromButtonStart && SteamVrSettings is { } steamVr && steamVr.AutoLaunchOnButton() != false
                ? "SteamVR's setting changed back, so a controller button can start SteamVR again. Turn the switch off and on on the Lighthouse page."
                : null;
        }
        catch (Exception e) when (e is SteamVrSettingsException or IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    public event Action? ProfilesChanged;

    public event Action? DevicesChanged;

    public void NotifyDevicesChanged() => _ = OnDevicesChangedAsync();

    private async Task OnDevicesChangedAsync()
    {
        await Task.Run(RefreshModels);
        DevicesChanged?.Invoke();
        if (radio.State == BluetoothState.Missing)
        {
            _ = Task.Run(StartRadioAsync);
        }
    }

    private void RefreshModels()
    {
        foreach (var pimax in Modules.Values.OfType<PimaxModule>())
        {
            try
            {
                pimax.RefreshModel();
            }
            catch (Exception e)
            {
                AppLog.Write("Couldn't look the Pimax headset's model up", e);
            }
        }
    }

    public void NotifyProfilesChanged()
    {
        ProfilesChanged?.Invoke();
        _ = Task.Run(Session.PlaceStandIn);
        _ = Task.Run(MatchLayerRegistrationAsync);
    }

    private readonly SemaphoreSlim layerRegistration = new(1, 1);

    private async Task MatchLayerRegistrationAsync()
    {
        await layerRegistration.WaitAsync();
        try
        {
            var profiles = Profiles.Load().Profiles;
            foreach (var layer in DirectModes.Values)
            {
                var used = profiles.Any(p => PartsOf(p).DirectMode == layer);
                if (await layer.SetRegisteredAsync(Context, used, CancellationToken.None) is { } problem)
                {
                    AppLog.Write($"The {layer.DisplayName} direct-mode layer couldn't be {(used ? "registered" : "taken out")}: {problem}");
                }
            }
        }
        catch (Exception e)
        {
            AppLog.Write("Matching the direct-mode layers to the profiles failed", e);
        }
        finally
        {
            layerRegistration.Release();
        }
    }

    public IReadOnlyDictionary<string, IHeadsetModule> Modules { get; }

    public IReadOnlyDictionary<DirectModeKind, IDirectMode> DirectModes { get; }

    public (IHeadsetModule? Module, IDirectMode? DirectMode) PartsOf(Profile profile)
    {
        var module = profile.ModuleId is { } id ? Modules.GetValueOrDefault(id) : null;
        return (module, DirectModes.GetValueOrDefault(profile.DirectModeWith(module)));
    }

    public Task<T> WithDisplayFreeAsync<T>(Profile profile, Func<IDirectMode, CancellationToken, Task<T>> work)
    {
        var (module, directMode) = PartsOf(profile);
        if (directMode is null)
        {
            throw new MiradorException("This profile uses no direct-mode layer.");
        }

        return Session.RunWhileStoppedAsync(
            module?.Id ?? "",
            async (_, ct) =>
            {
                if (module is not null)
                {
                    await module.FreeDisplayAsync(Context, ct).ConfigureAwait(false);
                }

                return await work(directMode, ct).ConfigureAwait(false);
            },
            CancellationToken.None);
    }

    public SessionController Session { get; }

    public ElevatedConsent ElevatedConsent { get; }

    public bool ElevatedMonadoAllowed()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User is { } sid && ElevatedConsent.IsAllowed(sid);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public bool SteamVrChangedSinceAllowed => steamVrChanged;

    public void SteamVrAllowedAgain() => steamVrChanged = false;

    public async Task<string?> TakeBackUnneededConsentAsync()
    {
        try
        {
            if (!ElevatedMonadoAllowed() || ElevatedChoice.ConsentNeeded(Profiles.Load()))
            {
                return null;
            }

            var reply = await Helper.SendAsync(HelperCommand.RevokeElevatedMonado, CancellationToken.None);
            return reply.Ok ? null : $"Mirador couldn't take back the consent for Monado to run as administrator: {reply.Error}";
        }
        catch (Exception e)
        {
            AppLog.Write("Taking back the consent for Monado as administrator failed", e);
            return $"Mirador couldn't take back the consent for Monado to run as administrator ({e.Message.Trim().TrimEnd('.')}).";
        }
    }

    public async Task<string?> ForgetUnneededApprovalsAsync()
    {
        try
        {
            var keep = Profiles.Load().Profiles
                .Where(p => p.Monado.Kind is SourceKind.Branch or SourceKind.Folder || p.ExtraEnvironment.Count > 0)
                .Select(p => p.Id)
                .ToHashSet(StringComparer.Ordinal);
            var listed = await Helper.SendAsync(new HelperRequest(HelperCommand.ListApprovedBuilds), CancellationToken.None);
            if (!listed.Ok)
            {
                AppLog.Write($"Listing the approved builds failed: {listed.Error}");
                return null;
            }

            foreach (var id in (listed.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Where(id => !keep.Contains(id)))
            {
                var reply = await Helper.SendAsync(new HelperRequest(HelperCommand.ForgetApprovedBuild, Build: id), CancellationToken.None);
                if (!reply.Ok)
                {
                    AppLog.Write($"Taking back the approval of profile {id} failed: {reply.Error}");
                    return $"Mirador couldn't take back an approved build no profile uses any more: {reply.Error}";
                }
            }

            return null;
        }
        catch (Exception e)
        {
            AppLog.Write("Taking back the approvals no profile needs failed", e);
            return null;
        }
    }

    public bool IsInstalledCopy(bool askHelper)
    {
        if (askHelper)
        {
            helperInstallDir = AskHelperInstallDir();
        }

        return InstalledMirador.IsInstalledCopy(
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mirador.exe"),
            helperInstallDir ?? RegisteredInstallDir());
    }

    private string? AskHelperInstallDir()
    {
        try
        {
            var reply = Helper.SendAsync(HelperCommand.GetInstallFolder, CancellationToken.None).GetAwaiter().GetResult();
            return reply.Ok && !string.IsNullOrWhiteSpace(reply.Value) ? reply.Value : null;
        }
        catch (Exception e)
        {
            AppLog.Write("Couldn't ask the helper where it is installed", e);
            return null;
        }
    }

    private static string? RegisteredInstallDir()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"SOFTWARE\Mirador");
            return key?.GetValue("AppPath") is string path && path.Length > 0 ? Path.GetDirectoryName(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    private void OnSteamVrChecked(bool changed, string? reason)
    {
        steamVrChanged = changed;
        if (changed)
        {
            AppLog.Write($"Monado runs as you this time: {reason ?? "SteamVR's Lighthouse driver changed since you allowed Monado as administrator."}");
        }
    }

    public string? RunsAsYouReason()
    {
        try
        {
            var profile = Profiles.Load().Selected;
            if (!profile.RunAsAdministrator)
            {
                return null;
            }

            if (!ElevatedMonadoAllowed())
            {
                return IsInstalledCopy(askHelper: false) ? NotAllowedReason : ElevatedChoice.NotInstalledCopy;
            }

            var module = profile.ModuleId is { } id ? Modules.GetValueOrDefault(id) : null;
            var reason = ElevatedChoice.RunsAsYouReason(
                profile, module, IsInstalledCopy(askHelper: false), ApprovedBuildCurrent(profile), VariablesApproved(profile));

            if (reason == ElevatedChoice.NotApproved && Approval(profile) is not null)
            {
                reason = ElevatedChoice.BuildChanged;
            }

            if (reason == ElevatedChoice.ExtraVariables && Approval(profile) is { } record
                && (record.Variables.Count > 0 || profile.Monado.Kind != SourceKind.Mirador))
            {
                reason = ElevatedChoice.VariablesChanged;
            }

            return reason ?? (steamVrChanged ? SteamVrChangedReason : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string CurrentSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? "unknown";
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "unknown";
        }
    }

    public StationManager Stations { get; }

    public string StandInExe => Path.Combine(AppDir, "standin", StandIn.FileName);

    public bool CloseToTray()
    {
        try
        {
            return Settings.Load().CloseToTray;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public bool MinimizeToTray()
    {
        try
        {
            return Settings.Load().MinimizeToTray;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IReadOnlyList<string> StartupNotices => notices;

    public OpenVrGames OpenVr { get; }

    public FolderWatch OpenVrWatch { get; }

    public OpenCompositeInstall OpenComposite { get; }

    public static AppHost Create(string appDir, AppPaths paths)
    {
        var host = new AppHost(appDir, paths);

        _ = Task.Run(host.RefreshModels);

        host.FirstRun = !File.Exists(paths.ProfilesFile);
        host.CheckStore("profiles", paths.ProfilesFile, () => host.Profiles.Load());
        _ = Task.Run(host.MatchLayerRegistrationAsync);

        _ = Task.Run(async () =>
        {
            if (await host.TakeBackUnneededConsentAsync() is { } problem)
            {
                AppLog.Write(problem);
            }
        });
        host.CheckStore("Monado settings", paths.MonadoSettingsFile, () => host.MonadoSettings.Load());
        host.CheckStore("settings", paths.SettingsFile, () => host.Settings.Load());

        host.CheckStore("base stations", paths.BaseStationsFile, () => host.stationStore.Load());
        host.CheckStore("room setups", paths.RoomSetupFile, () => host.RoomSetups.All());
        _ = Task.Run(host.StartStationsAsync);
        _ = Task.Run(() => host.IsInstalledCopy(askHelper: true));
        return host;
    }

    public bool ApprovedBuildCurrent(Profile profile)
    {
        if (profile.Monado.Kind is not (SourceKind.Branch or SourceKind.Folder))
        {
            return false;
        }

        try
        {
            return Locator.Locate(profile).Approved is not null;
        }
        catch (Exception e) when (e is MiradorException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool ElevationFixVerified(Profile profile, MonadoInstall install) =>
        VerifyResult.Load(VerifyResultFile(profile.Id)) is { HasElevationFix: true, StrictPipe: true } result && result.Key == VerifyBuild.KeyFor(install);

    public bool HasApproval(Profile profile) => Approval(profile) is not null;

    public bool VariablesApproved(Profile profile)
    {
        if (profile.Monado.Kind == SourceKind.Mirador && profile.ExtraEnvironment.Count == 0)
        {
            return true;
        }

        return Approval(profile) is { } record && ElevatedChoice.SameVariables(profile.ExtraEnvironment, record.Variables);
    }

    private ApprovedBuild? Approval(Profile profile)
    {
        try
        {
            if (AllowElevatedCommand.CurrentUser()?.Value is not { } owner || new RegistryApprovedBuildStore().Get(owner, profile.Id) is not { } record)
            {
                return null;
            }

            var bundled = record.Commit == ApprovedBuilds.BundledCommit;
            return profile.Monado.Kind switch
            {
                SourceKind.Mirador => bundled ? record : null,
                SourceKind.Branch or SourceKind.Folder => bundled ? null : record,
                _ => null,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private ApprovedBuild? ApprovedCopyOf(Profile profile, MonadoInstall install)
    {
        try
        {
            return AllowElevatedCommand.CurrentUser()?.Value is { } owner
                && new RegistryApprovedBuildStore().Get(owner, profile.Id) is { } approved
                && Ipc.ApprovedBuilds.Matches(approved, Path.GetDirectoryName(install.Manifest)!, buildHashes.HashOf)
                && Ipc.ApprovedBuilds.NamesOwnDll(approved)
                ? approved
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public async Task<string?> ApproveBuildAsync(Profile profile, bool alsoAllow = false)
    {
        await approvalGate.WaitAsync();
        try
        {
            return await ApproveNowAsync(profile, alsoAllow);
        }
        finally
        {
            approvalGate.Release();
        }
    }

    private readonly SemaphoreSlim approvalGate = new(1, 1);

    public bool Approving => approvalGate.CurrentCount == 0;

    private async Task<string?> ApproveNowAsync(Profile profile, bool alsoAllow)
    {
        if (ApprovalTarget(profile, out var commit, out var folder) is { } problem)
        {
            return problem;
        }

        var variablesOnly = folder != AppArgs.Bundled && await Task.Run(() => ApprovedBuildCurrent(profile)
            && Approval(profile) is { } record && ApprovedBuilds.Verify(record, AppDir) is null);
        if (folder != AppArgs.Bundled && !variablesOnly
            && Session.State != SessionState.Stopped && Session.CurrentProfile?.Profile.Id == profile.Id)
        {
            return "Stop Monado first: it runs from this profile's approved copy, which the approval replaces.";
        }

        var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mirador.exe"))
        {
            Verb = "runas",
            UseShellExecute = true,
        };
        if (alsoAllow)
        {
            info.ArgumentList.Add(AllowElevatedCommand.Flag);
        }

        foreach (var argument in variablesOnly ? [AppArgs.ApproveVariablesFlag, profile.Id] : new[] { AppArgs.ApproveBuildFlag, profile.Id, commit, folder })
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var variable in profile.ExtraEnvironment)
        {
            info.ArgumentList.Add($"{variable.Name}={variable.Value}");
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(info);
            if (process is null)
            {
                return "The approval couldn't be started.";
            }

            await process.WaitForExitAsync();
            if (process.ExitCode == 0 && alsoAllow)
            {
                SteamVrAllowedAgain();
            }

            return process.ExitCode == 0 ? null : "The build couldn't be approved; mirador.log says why.";
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == ElevatedRuntime.PromptCancelled)
        {
            return ApprovalCancelled;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return $"The approval couldn't be started ({e.Message.TrimEnd('.')}).";
        }
    }

    private Func<Task>? NewBuildApproval(string profileId)
    {
        try
        {
            if (Profiles.Load().Profiles.FirstOrDefault(p => p.Id == profileId) is not { } profile || !WouldApprove(profile))
            {
                return null;
            }

            return async () =>
            {
                try
                {
                    if (await ApproveIfNeededAsync(profile) is { } problem)
                    {
                        AppLog.Write($"Approving the new build: {problem}");
                    }
                }
                catch (Exception e)
                {
                    AppLog.Write("Approving the new build failed", e);
                }
            };
        }
        catch (Exception e)
        {
            AppLog.Write("Approving the new build failed", e);
            return null;
        }
    }

    public async Task<string?> ApproveIfNeededAsync(Profile profile)
    {
        if (!WouldApprove(profile))
        {
            return null;
        }

        await approvalGate.WaitAsync();
        try
        {
            return WouldApprove(profile) ? await ApproveNowAsync(profile, alsoAllow: false) : null;
        }
        finally
        {
            approvalGate.Release();
        }
    }

    public bool WouldApprove(Profile profile) =>
        profile.RunAsAdministrator && !FullyApproved(profile) && ApprovalTarget(profile, out _, out _) is null
        && IsInstalledCopy(askHelper: false) && ElevatedMonadoAllowed();


    public string? ApprovalTarget(Profile profile, out string commit, out string folder)
    {
        (commit, folder) = (AppArgs.Bundled, AppArgs.Bundled);
        if (ElevatedChoice.ApprovalProblem(profile) is { } problem)
        {
            return problem;
        }

        if (profile.Monado.Kind == SourceKind.Mirador)
        {
            return profile.ExtraEnvironment.Count == 0
                ? "This profile has no variables to approve: Mirador's Monado runs as administrator without an approval."
                : null;
        }

        MonadoInstall install;
        try
        {
            install = Locator.Locate(profile);
        }
        catch (MiradorException e)
        {
            return e.Message;
        }

        folder = Path.GetDirectoryName(install.Manifest)!;
        if (!File.Exists(Path.Combine(folder, "bin", "monado-service.exe")))
        {
            return "Only an installed build can be approved (Monado's install layout, as Mirador's branch builds are). Build it with \"cmake --install\", or let Mirador build the branch.";
        }

        commit = Core.Build.BuildInfo.Load(folder)?.Commit is { } c && ApprovedBuilds.IsGitCommit(c) ? c : "folder";
        return null;
    }

    public bool FullyApproved(Profile profile) =>
        (profile.Monado.Kind == SourceKind.Mirador || ApprovedBuildCurrent(profile)) && VariablesApproved(profile);

    public async Task<(string Message, bool Ok)?> InstallMiradorsMonadoIfNeededAsync()
    {
        if (!Profiles.Load().Profiles.Any(p => p.Monado.Kind == SourceKind.Mirador)
            || Ipc.MiradorMonado.Read(Locator.MiradorRoot).Current is not null)
        {
            return null;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var newest = (await Ipc.MonadoReleaseList.FetchAsync(http, AppInfo.Version, CancellationToken.None))?.FirstOrDefault();
        if (newest is null)
        {
            return ("The bundled Monado couldn't be downloaded: GitHub didn't answer, or no build is published yet. Try again on the Profiles page.", false);
        }

        var reply = await Helper.SendAsync(new Ipc.HelperRequest(Ipc.HelperCommand.InstallMonado, Version: newest.Version), CancellationToken.None);
        NotifyProfilesChanged();
        return reply.Ok
            ? ($"The bundled Monado {newest.Version} is installed.", true)
            : ($"The bundled Monado couldn't be installed: {reply.Error}", false);
    }

    public static void RecordAppPath()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Mirador");
            key.SetValue("AppPath", Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mirador.exe"), RegistryValueKind.String);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            AppLog.Write("Couldn't record where Mirador is for games", e);
        }
    }

    public void StartPipe(Func<string, int?, CancellationToken, Task<string>> handle)
    {
        var server = new AppPipeServer(AppPipe.CurrentName(), handle);
        server.Failed += e => AppLog.Write("The app pipe stopped listening", e);
        server.HandlerFailed += e => AppLog.Write("Answering the app pipe failed", e);
        try
        {
            server.Start();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            throw new MiradorException(
                $"Another program holds the name Mirador listens on for games ({AppPipe.CurrentName()}), so games can't start Monado through Mirador. Close other copies of Mirador, or restart Windows.");
        }

        pipe = server;
    }

    public async ValueTask DisposeAsync()
    {
        if (pipe is not null)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }

        Stations.QuittingNow();
        Session.EndStarts();
        try
        {
            await Session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await Stations.FlushAsync(StationsLimit).ConfigureAwait(false);
        }
        finally
        {
            await Xr.MonadoStoppedAsync().ConfigureAwait(false);
            await xrPipe.DisposeAsync().ConfigureAwait(false);
            OpenVrWatch.Dispose();
            launcher.Dispose();
        }
    }

    private async Task StartStationsAsync()
    {
        await StartRadioAsync().ConfigureAwait(false);
        try
        {
            await Stations.StartupAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            AppLog.Write("Base stations: Mirador's start with the base stations failed", e);
        }
    }

    private async Task StartRadioAsync()
    {
        try
        {
            await radio.StartAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            AppLog.Write("Base stations: Mirador couldn't look at the PC's Bluetooth", e);
        }
    }

    private AppSettings StationSettings()
    {
        try
        {
            return Settings.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Base stations: Mirador couldn't read its settings ({e.Message.Trim().TrimEnd('.')}); it uses the defaults");
            return new AppSettings();
        }
    }

    private void CheckStore(string what, string path, Action load)
    {
        var bad = path + ".bad";
        var before = Stamp(bad);
        try
        {
            load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notices.Add($"Mirador couldn't read its {what} ({path}): {e.Message.Trim().TrimEnd('.')}. It uses the defaults until it can.");
            return;
        }

        if (Stamp(bad) is { } after && after != before)
        {
            notices.Add($"Mirador's {what} file couldn't be read, so they were reset. The old file is {bad}.");
        }
    }

    private static (long Length, DateTime Written, DateTime Created)? Stamp(string file)
    {
        var info = new FileInfo(file);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc) : null;
    }
}
