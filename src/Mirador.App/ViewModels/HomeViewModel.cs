using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.BaseStations;
using Mirador.Core.Diagnostics;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.OpenVr;
using Mirador.Core.Profiles;
using Mirador.Core.Runtime;
using Mirador.Core.Session;

namespace Mirador.App.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    private static readonly TimeSpan DevicePollInterval = TimeSpan.FromSeconds(3);

    private static readonly HashSet<string> SessionLineKeys = ["monado", "apps", "tracking", "standin"];

    private readonly AppHost host;
    private readonly DispatcherQueue ui;
    private CancellationTokenSource? checking;
    private bool loading;
    private string? openCompositeProblem;
    private IDisposable? listening;
    private bool pageShown;
    private int stationsRefreshQueued;
    private CancellationTokenSource? devicePolling;
    private bool readingDevices;

    private IReadOnlyList<StatusLine> deviceLines = [];

    private IReadOnlyList<MonadoDevice> monadoDevices = [];

    private Profile? selected;

    public HomeViewModel(AppHost host, DispatcherQueue ui)
    {
        this.host = host;
        this.ui = ui;
        foreach (var notice in host.StartupNotices)
        {
            Notices.Add(new NoticeItem("startup", "Settings", notice, InfoBarSeverity.Warning, IsClosable: true));
        }

        host.DevicesChanged += () => ui.TryEnqueue(() => _ = RunChecksAsync());

        host.Session.Changed += () => ui.TryEnqueue(OnSessionChanged);

        host.OpenVrWatch.Changed += () => ui.TryEnqueue(RefreshRuntime);
        host.Session.StatusChanged += () => ui.TryEnqueue(RefreshStatus);
        host.Stations.Changed += OnStationsChanged;
        host.Session.Warning += w => ui.TryEnqueue(() => AddWarning(w));
        host.Session.Ended += end => ui.TryEnqueue(() => OnEnded(end));
        host.RoomSetupApplied += problem => ui.TryEnqueue(() => ShowRoomSetupProblem(problem));
        host.Xr.ProblemChanged += () => ui.TryEnqueue(ShowHeadsetProblem);
        App.Current.StartFailed += message =>
        {
            LastFailure = null;
            LastFailure = message;
        };
        LoadProfiles();
        OnSessionChanged();
        RefreshStations();
        CheckSteamVrButtonStart();
        CheckRoomSetup();
    }

    public ObservableCollection<ProfileItem> Profiles { get; } = [];

    public ObservableCollection<StatusItem> Status { get; } = [];

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    [ObservableProperty]
    public partial bool ShowDevices { get; set; }

    [ObservableProperty]
    public partial bool HasNoDevices { get; set; }

    [ObservableProperty]
    public partial bool ShowStations { get; set; }

    [ObservableProperty]
    public partial StatusItem StationsRow { get; set; } = new("Base stations", "", StatusLevel.Info);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStationsIcon))]
    public partial bool StationsBusy { get; set; }

    public bool ShowStationsIcon => !StationsBusy;

    [ObservableProperty]
    public partial bool ShowStationButtons { get; set; }

    [ObservableProperty]
    public partial bool CanSwitchStations { get; set; }

    public ObservableCollection<NoticeItem> Notices { get; } = [];

    [ObservableProperty]
    public partial ProfileItem? SelectedProfile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeProfile))]
    public partial SessionState State { get; set; }

    [ObservableProperty]
    public partial string StartStopLabel { get; set; } = "Start";

    [ObservableProperty]
    public partial bool CanStartStop { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    public partial string? LastFailure { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuntimeNotice))]
    public partial string? RuntimeNotice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunsAsYouNotice), nameof(AllowLabel))]
    public partial string? RunsAsYouNotice { get; set; }

    [ObservableProperty]
    public partial bool OffersAllowAgain { get; set; }

    public string AllowLabel => RunsAsYouNotice == AppHost.NotAllowedReason ? "Allow" : "Allow again";

    [ObservableProperty]
    public partial bool CanAllowAgain { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDirectModeNotice))]
    public partial string? DirectModeNotice { get; set; }

    public bool HasDirectModeNotice => DirectModeNotice is not null;

    [ObservableProperty]
    public partial bool CanFixDirectMode { get; set; } = true;

    [ObservableProperty]
    public partial StatusItem OpenXrRow { get; set; } = new("OpenXR runtime", "", StatusLevel.Info);

    [ObservableProperty]
    public partial bool CanSetRuntime { get; set; }

    [ObservableProperty]
    public partial bool ShowOpenVr { get; set; }

    [ObservableProperty]
    public partial StatusItem OpenVrRow { get; set; } = new("OpenVR runtime", "", StatusLevel.Info);

    [ObservableProperty]
    public partial bool CanSetOpenComposite { get; set; }

    [ObservableProperty]
    public partial bool CanGetOpenComposite { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuntimeProblem))]
    public partial string? RuntimeProblem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenVrProblem))]
    public partial string? OpenVrProblem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenVrWarning))]
    public partial string? OpenVrWarning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    public partial string? UpdateText { get; set; }

    [ObservableProperty]
    public partial Uri? UpdateUrl { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenCompositeUpdate))]
    public partial string? OpenCompositeUpdateText { get; set; }

    [ObservableProperty]
    public partial bool CanUpdateOpenComposite { get; set; }

    private Release? newerOpenComposite;

    public bool CanChangeProfile => State == SessionState.Stopped;

    public bool HasFailure => LastFailure is not null;

    public bool HasRuntimeNotice => RuntimeNotice is not null;

    public bool HasRunsAsYouNotice => RunsAsYouNotice is not null;

    public bool HasRuntimeProblem => RuntimeProblem is not null;

    public bool HasOpenVrProblem => OpenVrProblem is not null;

    public bool HasOpenVrWarning => OpenVrWarning is not null;

    public bool HasUpdate => UpdateText is not null;

    public bool HasOpenCompositeUpdate => OpenCompositeUpdateText is not null;

    public void Refresh()
    {
        LoadProfiles();
        RefreshRuntime();
        RefreshStations();
        CheckRoomSetup();
        GpuSchedulingNotice = Core.Gpu.GpuScheduling.Notice(Core.Gpu.GpuScheduling.Now());
        _ = RunChecksAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGpuSchedulingNotice))]
    public partial string? GpuSchedulingNotice { get; set; }

    public bool HasGpuSchedulingNotice => GpuSchedulingNotice is not null;

    public void OnWindowActivated()
    {
        if (pageShown)
        {
            RefreshRuntime();
        }
    }

    public void SetShown(bool shown)
    {
        pageShown = shown;
        UpdateListening();
        UpdateDevicePolling();
    }

    private void UpdateDevicePolling()
    {
        var want = pageShown && State is SessionState.Starting or SessionState.Running;
        if (want == (devicePolling is not null))
        {
            return;
        }

        if (want)
        {
            devicePolling = new CancellationTokenSource();
            _ = PollDevicesAsync(devicePolling.Token);
        }
        else
        {
            devicePolling!.Cancel();
            devicePolling.Dispose();
            devicePolling = null;
            ShowDeviceLines([], []);
        }
    }

    private async Task PollDevicesAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(DevicePollInterval);
        try
        {
            do
            {
                try
                {
                    await ReadDevicesAsync(ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    AppLog.Write("Mirador couldn't read Monado's devices", e);
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReadDevicesAsync(CancellationToken ct)
    {
        if (readingDevices)
        {
            return;
        }

        readingDevices = true;
        try
        {
            IReadOnlyList<StatusLine> lines;
            IReadOnlyList<MonadoDevice> devices = [];
            if (host.Session.Monado is { } monado)
            {
                try
                {
                    devices = await Task.Run(() => monado.Devices(), CancellationToken.None);
                    lines = DeviceStatus.Lines(devices);
                }
                catch (MiradorException e)
                {
                    lines = [new StatusLine("devices", "Devices", StatusLevel.Warning, e.Message)];
                    if (!lines.SequenceEqual(deviceLines))
                    {
                        AppLog.Write("Mirador couldn't read Monado's devices", e);
                    }
                }
            }
            else if (host.Session.MonadoClientProblem is { } problem)
            {
                lines = [new StatusLine("devices", "Devices", StatusLevel.Info, problem)];
            }
            else
            {
                lines = [];
            }

            if (!ct.IsCancellationRequested)
            {
                ShowDeviceLines(lines, devices);
            }
        }
        finally
        {
            readingDevices = false;
        }
    }

    private void ShowDeviceLines(IReadOnlyList<StatusLine> lines, IReadOnlyList<MonadoDevice> devices)
    {
        if (!lines.SequenceEqual(deviceLines) || !devices.SequenceEqual(monadoDevices))
        {
            deviceLines = lines;
            monadoDevices = devices;
            RefreshStatus();
        }
    }

    public void ShowUpdates(UpdateReport? report)
    {
        if (report is not { Reached: true })
        {
            return;
        }

        UpdateText = null;
        OpenCompositeUpdateText = null;
        if (report.NewerMirador is { } mirador)
        {
            ShowUpdate(mirador.Tag, mirador.Url);
        }

        newerOpenComposite = report.NewerOpenComposite;
        if (newerOpenComposite is { } openComposite)
        {
            OpenCompositeUpdateText = $"OpenLamina {UpdateReport.Plain(openComposite.Tag)} is out.";
            CanUpdateOpenComposite = App.Current.Updates.CanUpdateOpenComposite && openComposite.InstallerUrl is not null;
        }
        else
        {
            CanUpdateOpenComposite = false;
        }
    }

    public void ShowUpdate(string tag, string url)
    {
        UpdateText = $"Mirador {tag} is out.";
        UpdateUrl = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    [RelayCommand]
    private async Task UpdateOpenCompositeAsync()
    {
        if (newerOpenComposite is not { } release)
        {
            return;
        }

        try
        {
            var message = await App.Current.Updates.UpdateOpenCompositeAsync(release);
            ShowNotice("opencomposite-update", "OpenLamina", message, InfoBarSeverity.Informational);
        }
        catch (Exception e)
        {
            AppLog.Write("Updating OpenLamina failed", e);
            ShowNotice("opencomposite-update", "OpenLamina", $"Updating OpenLamina failed: {e.Message.Trim()}", InfoBarSeverity.Error);
        }
    }

    public void Dismiss(NoticeItem notice) => Notices.Remove(notice);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartStopAsync()
    {
        try
        {
            if (host.Session.State == SessionState.Stopped)
            {
                LastFailure = null;
                await App.Current.StartAsync(StartReason.Button);
            }
            else
            {
                await host.Session.StopAsync(CancellationToken.None);
            }
        }
        catch (Exception e)
        {
            AppLog.Write("Start or Stop failed", e);
            LastFailure = e.Message;
        }
    }

    [RelayCommand]
    private async Task SetRuntimeAsync()
    {
        RuntimeProblem = null;
        MonadoInstall install;
        try
        {
            install = host.Session.CurrentInstall ?? host.Locator.Locate(SelectedSet().Selected);
        }
        catch (Exception e) when (e is MiradorException or IOException or UnauthorizedAccessException)
        {
            RuntimeProblem = e.Message;
            return;
        }

        if (RuntimeManifest.Validate(install.Manifest) is { } problem)
        {
            RuntimeProblem = problem;
            return;
        }

        CanSetRuntime = false;
        try
        {
            var profile = host.Session.CurrentProfile?.Profile ?? SelectedSet().Selected;

            if (!install.IsBundled && !host.ApprovedBuildCurrent(profile)
                && await host.ApproveBuildAsync(profile.RunAsAdministrator ? profile : profile with { ExtraEnvironment = [] }) is { } refused)
            {
                RuntimeProblem = refused == AppHost.ApprovalCancelled ? "The prompt was cancelled. The runtime is unchanged." : refused;
                return;
            }

            RuntimeProblem = await RuntimeSwitch.SetAsync(
                install, host.Helper, host.Runtime, host.ApprovedBuildCurrent(profile) ? profile.Id : null, host.HasApproval(profile));
        }
        catch (Exception e)
        {
            AppLog.Write("Setting the OpenXR runtime failed", e);
            RuntimeProblem = $"Setting the OpenXR runtime failed ({e.Message.Trim().TrimEnd('.')}); the runtime is unchanged.";
        }
        finally
        {
            RefreshRuntime();
        }
    }

    partial void OnSelectedProfileChanged(ProfileItem? value)
    {
        if (loading || value is null)
        {
            return;
        }

        LastFailure = null;
        try
        {
            var set = host.Profiles.Load();
            if (set.Profiles.FirstOrDefault(p => p.Id == value.Id) is { } picked)
            {
                if (set.SelectedId != value.Id)
                {
                    host.Profiles.Save(set.Select(value.Id));
                    host.NotifyProfilesChanged();
                }

                selected = picked;
                CheckRoomSetup();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowNotice("profiles", "Profiles", $"Mirador couldn't save the profile choice ({e.Message.Trim().TrimEnd('.')}).", InfoBarSeverity.Error);
            LoadProfiles();
        }

        RefreshRuntime();
        RefreshStations();
        _ = RunChecksAsync();
    }

    [RelayCommand]
    private void SwitchStationsOn() => SwitchStations(() => StationCommand.On);

    [RelayCommand]
    private void SwitchStationsOff() => SwitchStations(host.Stations.OffCommand);

    private async void SwitchStations(Func<StationCommand> command)
    {
        try
        {
            await host.Stations.SwitchSelectedAsync(command(), CancellationToken.None);
        }
        catch (Exception e)
        {
            AppLog.Write("Switching the base stations failed", e);
        }
    }

    private void OnStationsChanged()
    {
        if (Interlocked.Exchange(ref stationsRefreshQueued, 1) == 0 && !ui.TryEnqueue(StationsRefreshQueued))
        {
            Volatile.Write(ref stationsRefreshQueued, 0);
        }
    }

    private void StationsRefreshQueued()
    {
        Volatile.Write(ref stationsRefreshQueued, 0);
        RefreshStations();
    }

    private void RefreshStations()
    {
        if (selected is { } profile)
        {
            var uses = profile.ModuleId is { } id && host.Modules.TryGetValue(id, out var module) && module.UsesSteamVrLighthouse;
            ShowStations = profile.UsesBaseStations(uses);
        }

        if (ShowStations)
        {
            try
            {
                var summary = host.Stations.Summary;
                StationsRow = new StatusItem("Base stations", summary.Text, summary.Level);
                StationsBusy = summary.Busy;
                var bluetooth = host.Stations.Bluetooth;
                ShowStationButtons = bluetooth != BluetoothState.Missing;
                CanSwitchStations = bluetooth == BluetoothState.Ready
                    && host.Stations.Running is null
                    && host.Stations.Stations.Any(s => s.Station.Selected);
            }
            catch (Exception e)
            {
                AppLog.Write("Reading the base stations failed", e);
            }
        }

        UpdateListening();
    }

    private void UpdateListening()
    {
        var want = pageShown && ShowStations;
        if (want == (listening is not null))
        {
            return;
        }

        if (want)
        {
            try
            {
                listening = host.Stations.Listen();
            }
            catch (Exception e)
            {
                AppLog.Write("Listening to the base stations failed", e);
            }
        }
        else
        {
            listening!.Dispose();
            listening = null;
        }
    }

    private ProfileSet SelectedSet() => host.Profiles.Load();

    private void LoadProfiles()
    {
        loading = true;
        try
        {
            var set = SelectedSet();
            Profiles.Clear();
            foreach (var p in set.Profiles)
            {
                Profiles.Add(new ProfileItem(p.Id, p.Name));
            }

            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == set.SelectedId);
            selected = set.Profiles.FirstOrDefault(p => p.Id == set.SelectedId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowNotice("profiles", "Profiles", $"Mirador couldn't read its profiles ({e.Message.Trim().TrimEnd('.')}).", InfoBarSeverity.Error);
        }
        finally
        {
            loading = false;
        }
    }

    private void OnSessionChanged()
    {
        var previous = State;
        State = host.Session.State;
        (StartStopLabel, CanStartStop) = State switch
        {
            SessionState.Running => ("Stop", true),
            SessionState.Starting => ("Starting…", true),
            SessionState.Stopping => ("Stopping…", false),
            _ => ("Start", true),
        };
        if (State == SessionState.Starting && previous == SessionState.Stopped)
        {
            ClearWarnings();
            RemoveNotice("ended");
            CheckSteamVrButtonStart();
        }

        if (State != SessionState.Stopped)
        {
            DirectModeNotice = null;
        }

        RefreshStatus();
        UpdateDevicePolling();
        if (devicePolling is { } polling)
        {
            _ = ReadDevicesAsync(polling.Token);
        }

        RefreshRuntime();
        if (State == SessionState.Stopped && previous != SessionState.Stopped)
        {
            ClearWarnings();
            _ = RunChecksAsync();
        }
    }

    private void RefreshStatus()
    {
        var session = host.Session.Status.ToList();
        var moduleLinesAt = session.FindLastIndex(line => SessionLineKeys.Contains(line.Key)) + 1;
        session.InsertRange(moduleLinesAt, deviceLines);
        var lines = session.Select(StatusItem.From).ToList();
        if (!lines.SequenceEqual(Status))
        {
            Status.Clear();
            foreach (var line in lines)
            {
                Status.Add(line);
            }
        }

        var tracked = host.Session.Devices;
        var items = DeviceStatus.Controllers(tracked, monadoDevices).Select(DeviceItem.From).ToList();
        ShowDevices = tracked is not null || items.Count > 0;
        if (!items.SequenceEqual(Devices))
        {
            Devices.Clear();
            foreach (var item in items)
            {
                Devices.Add(item);
            }
        }

        HasNoDevices = items.Count == 0;
    }

    private void RefreshRuntime()
    {
        MonadoInstall? install = host.Session.CurrentInstall;
        var profile = install is not null ? host.Session.CurrentProfile?.Profile : null;
        string? noMonado = null;
        if (install is null)
        {
            try
            {
                profile = SelectedSet().Selected;
                install = host.Locator.Locate(profile);
            }
            catch (Exception e) when (e is MiradorException or IOException or UnauthorizedAccessException)
            {
                install = null;
                noMonado = e.Message;
            }
        }

        if (State == SessionState.Stopped)
        {
            CanStartStop = noMonado is null;
            if (noMonado is null)
            {
                RemoveNotice("nomonado");
            }
            else if (Notices.FirstOrDefault(n => n.Key == "nomonado")?.Message != noMonado)
            {
                RemoveNotice("nomonado");
                Notices.Insert(0, new NoticeItem("nomonado", "No Monado yet", noMonado, InfoBarSeverity.Warning, IsClosable: false));
            }
        }

        var active = host.Runtime.Active();
        var isActive = install is not null && host.Runtime.IsActive(install);
        OpenXrRow = new StatusItem("OpenXR runtime", RuntimeLine.Describe(active, install, isActive, (profile ?? SelectedSet().Selected).Name), isActive ? StatusLevel.Ok : StatusLevel.Info);
        CanSetRuntime = install is not null && !isActive;
        if (isActive)
        {
            RuntimeProblem = null;
        }

        RuntimeNotice = host.Session.RuntimeNotice;
        RunsAsYouNotice = host.RunsAsYouReason();
        OffersAllowAgain = RunsAsYouNotice is AppHost.SteamVrChangedReason or AppHost.NotAllowedReason;
        RefreshOpenVr(isActive, active);
    }

    [RelayCommand]
    private async Task AllowAgainAsync()
    {
        CanAllowAgain = false;
        try
        {
            var code = await ElevatedMonado.AllowAsync();
            if (code == 0)
            {
                host.SteamVrAllowedAgain();
                RemoveNotice("allow-again");
            }
            else if (code != ElevatedRuntime.PromptCancelled)
            {
                ShowNotice("allow-again", "Monado", "Mirador couldn't record the consent for Monado to run as administrator. The reason is in mirador.log.", InfoBarSeverity.Error);
            }
        }
        catch (Exception e)
        {
            AppLog.Write("Allowing Monado to run as administrator again failed", e);
            ShowNotice("allow-again", "Monado", $"Allowing Monado to run as administrator again failed ({e.Message.Trim().TrimEnd('.')}).", InfoBarSeverity.Error);
        }
        finally
        {
            CanAllowAgain = true;
            RefreshRuntime();
        }
    }

    private void RefreshOpenVr(bool openXrIsMonado, string? activeOpenXr)
    {
        ShowOpenVr = true;
        CanGetOpenComposite = !host.OpenVr.Available;
        OpenVrView view;
        try
        {
            view = host.OpenVr.View(openXrIsMonado, activeOpenXr);
        }
        catch (Exception e)
        {
            AppLog.Write("Reading where OpenVR games go failed", e);
            view = new OpenVrView("Unknown", false, null, $"Mirador couldn't read where OpenVR games go ({e.Message.Trim().TrimEnd('.')}).");
        }

        if (!view.OffersOpenComposite && view.Problem is null)
        {
            openCompositeProblem = null;
        }

        var level = view.Problem is not null ? StatusLevel.Error : view.OffersOpenComposite || CanGetOpenComposite ? StatusLevel.Info : StatusLevel.Ok;
        OpenVrRow = new StatusItem("OpenVR runtime", CanGetOpenComposite && view.Problem is null ? "OpenLamina isn't installed yet" : view.Runtime, level);
        CanSetOpenComposite = view.OffersOpenComposite && !CanGetOpenComposite;
        OpenVrWarning = view.Warning;
        OpenVrProblem = openCompositeProblem ?? view.Problem;
    }

    [RelayCommand]
    private async Task GetOpenCompositeAsync()
    {
        CanGetOpenComposite = false;
        try
        {
            var message = await App.Current.Updates.GetOpenCompositeAsync();
            openCompositeProblem = null;
            ShowNotice("opencomposite-update", "OpenLamina", message, InfoBarSeverity.Informational);
        }
        catch (Exception e)
        {
            AppLog.Write("Getting OpenLamina failed", e);
            openCompositeProblem = $"Getting OpenLamina failed: {e.Message.Trim()}";
        }

        RefreshRuntime();
    }

    [RelayCommand]
    private async Task SetOpenCompositeAsync()
    {
        CanSetOpenComposite = false;
        try
        {
            openCompositeProblem = await Task.Run(host.OpenVr.UseMonado);
        }
        catch (Exception e)
        {
            AppLog.Write("Setting OpenLamina failed", e);
            openCompositeProblem = $"Setting OpenLamina failed ({e.Message.Trim().TrimEnd('.')}); nothing changed.";
        }

        if (openCompositeProblem is not null)
        {
            AppLog.Write("Setting OpenLamina: " + openCompositeProblem);
        }

        OpenVrProblem = null;
        RefreshRuntime();
    }

    private async Task RunChecksAsync()
    {
        checking?.Cancel();
        var cts = checking = new CancellationTokenSource();
        Profile profile;
        try
        {
            profile = SelectedSet().Selected;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        List<CheckResult> results = [];
        var (module, directMode) = host.PartsOf(profile);
        if (module is not null || directMode is not null)
        {
            try
            {
                if (directMode is not null)
                {
                    results.AddRange(await directMode.CheckAsync(host.Context, cts.Token));
                }

                if (module is not null)
                {
                    results.AddRange(await module.CheckAsync(host.Context, profile, cts.Token));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                AppLog.Write("The checks failed", e);
            }
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        for (var i = Notices.Count - 1; i >= 0; i--)
        {
            if (Notices[i].Key.StartsWith("check:", StringComparison.Ordinal))
            {
                Notices.RemoveAt(i);
            }
        }

        DirectModeNotice = State == SessionState.Stopped
            ? results.FirstOrDefault(r => r.Label == IDirectMode.HeadsetOnDesktopLabel)?.Text
            : null;
        foreach (var r in results.Where(r => r.Level is StatusLevel.Warning or StatusLevel.Error && r.Label != IDirectMode.HeadsetOnDesktopLabel))
        {
            Notices.Add(new NoticeItem(
                "check:" + r.Label,
                r.Label,
                r.Fix is null ? r.Text : $"{r.Text} {r.Fix}",
                r.Level == StatusLevel.Error ? InfoBarSeverity.Error : InfoBarSeverity.Warning,
                IsClosable: false));
        }
    }

    [RelayCommand]
    private async Task FixDirectModeAsync()
    {
        Profile profile;
        try
        {
            profile = SelectedSet().Selected;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        CanFixDirectMode = false;
        RemoveNotice("fix-direct-mode");
        try
        {
            var problem = await host.WithDisplayFreeAsync(profile, (directMode, ct) => directMode.FixAsync(host.Context, ct));
            if (problem is not null)
            {
                ShowNotice("fix-direct-mode", "Direct mode", problem, InfoBarSeverity.Error);
            }
        }
        catch (MiradorException e)
        {
            ShowNotice("fix-direct-mode", "Direct mode", e.Message, InfoBarSeverity.Error);
        }
        finally
        {
            CanFixDirectMode = true;
            await RunChecksAsync();
        }
    }

    private void OnEnded(SessionEnd end)
    {
        if (end.Message != "Stopped.")
        {
            ShowNotice("ended", "Monado stopped", end.Message, end.Problem ? InfoBarSeverity.Error : InfoBarSeverity.Informational);
        }
    }

    public void ShowMonadoNotice(string message, bool ok)
    {
        ShowNotice("miradors-monado", "Bundled Monado", message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        RefreshRuntime();
    }

    private void ShowHeadsetProblem()
    {
        if (host.Xr.Problem is { } problem)
        {
            ShowNotice("headset", "In the headset", problem, InfoBarSeverity.Warning);
        }
        else
        {
            RemoveNotice("headset");
        }
    }

    private void ShowRoomSetupProblem(string? problem)
    {
        if (problem is null)
        {
            RemoveNotice("room-setup");
        }
        else
        {
            ShowNotice("room-setup", "Room setup", problem, InfoBarSeverity.Warning);
        }
    }

    public void CheckRoomSetup() => _ = CheckRoomSetupAsync();

    private async Task CheckRoomSetupAsync()
    {
        var (missing, changed) = await Task.Run(host.RoomSetupWarnings);
        ShowState("room-setup-missing", missing);
        ShowState("room-setup-changed", changed);
    }

    private void ShowState(string key, string? warning)
    {
        var existing = Notices.FirstOrDefault(n => n.Key == key);
        if (warning is null)
        {
            RemoveNotice(key);
        }
        else if (existing is null)
        {
            Notices.Add(new NoticeItem(key, "Room setup", warning, InfoBarSeverity.Warning, IsClosable: false));
        }
        else if (existing.Message != warning)
        {
            Notices[Notices.IndexOf(existing)] = existing with { Message = warning };
        }
    }

    public void CheckSteamVrButtonStart()
    {
        if (host.SteamVrButtonStartWarning() is { } warning)
        {
            ShowNotice("steamvr-button", "SteamVR", warning, InfoBarSeverity.Warning);
        }
        else
        {
            RemoveNotice("steamvr-button");
        }
    }

    private void ShowNotice(string key, string title, string message, InfoBarSeverity severity)
    {
        RemoveNotice(key);
        Notices.Insert(0, new NoticeItem(key, title, message, severity, IsClosable: true));
    }

    private void RemoveNotice(string key)
    {
        for (var i = Notices.Count - 1; i >= 0; i--)
        {
            if (Notices[i].Key == key)
            {
                Notices.RemoveAt(i);
            }
        }
    }

    private void AddWarning(SessionWarning w)
    {
        var key = "warning:" + w.Key;
        var existing = Notices.FirstOrDefault(n => n.Key == key);
        var item = new NoticeItem(key, "While Monado runs", w.Text, InfoBarSeverity.Warning, IsClosable: false);
        if (existing is null)
        {
            Notices.Add(item);
        }
        else
        {
            Notices[Notices.IndexOf(existing)] = item;
        }
    }

    private void ClearWarnings()
    {
        for (var i = Notices.Count - 1; i >= 0; i--)
        {
            if (Notices[i].Key.StartsWith("warning:", StringComparison.Ordinal))
            {
                Notices.RemoveAt(i);
            }
        }
    }
}
