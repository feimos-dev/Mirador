using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.BaseStations;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Playspace;
using Mirador.Core.Session;

namespace Mirador.App.ViewModels;

public sealed partial class StationItem : ObservableObject
{
    private readonly LighthouseViewModel owner;
    private bool updating;

    public StationItem(LighthouseViewModel owner, string address)
    {
        this.owner = owner;
        Address = address;
    }

    public string Address { get; }

    public string Name { get; private set; } = "";

    public string? CustomName { get; private set; }

    public string? Id { get; private set; }

    [ObservableProperty]
    public partial string DisplayName { get; set; } = "";

    [ObservableProperty]
    public partial string AddressLine { get; set; } = "";

    [ObservableProperty]
    public partial string StateLine { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsV2 { get; set; }

    public bool IsV1 => !IsV2;

    [ObservableProperty]
    public partial bool CanIdentify { get; set; }

    [ObservableProperty]
    public partial string IdentifyHelp { get; set; } = "";

    public bool CanSwitch { get; private set; }

    public void Identify() => owner.Identify(this);

    public void PowerOn() => owner.Switch(this, StationCommand.On);

    public void PowerOff() => owner.SwitchOff(this);

    public void Forget() => owner.Forget(this);

    internal void Update(StationView view, bool looking, bool bluetoothReady)
    {
        var station = view.Station;
        var v2 = station.Version == StationVersion.V2;
        var on = v2 && view.Seen && view.Power == StationPower.On;
        updating = true;
        try
        {
            Name = station.Name;
            CustomName = station.CustomName;
            Id = station.Id;
            DisplayName = station.DisplayName;
            AddressLine = station.CustomName is null ? station.Address : $"{station.Name} · {station.Address}";
            StateLine = Describe(view, looking);
            IsSelected = station.Selected;
            IsV2 = v2;
            CanIdentify = on && bluetoothReady;
            IdentifyHelp = on ? "" : "Switch it on to make it blink.";
            CanSwitch = bluetoothReady;
        }
        finally
        {
            updating = false;
        }
    }

    private static string Describe(StationView view, bool looking)
    {
        if (view.Problem is not null)
        {
            return "Didn't answer";
        }

        if (!view.Seen)
        {
            return looking ? "Looking…" : "Not seen in the last scan";
        }

        if (view.Station.Version == StationVersion.V1)
        {
            return view.Station.Id is null ? "1.0 · Off takes a minute without its ID" : "1.0";
        }

        return view.Power switch
        {
            StationPower.On => "2.0 · On",
            StationPower.Waking => "2.0 · Waking up",
            StationPower.Asleep => "2.0 · Asleep",
            _ => "2.0 · State unknown",
        };
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!updating)
        {
            owner.SetSelected(this, value);
        }
    }

    partial void OnIsV2Changed(bool value) => OnPropertyChanged(nameof(IsV1));
}

public sealed partial class LighthouseViewModel : ObservableObject
{
    private readonly AppHost host;
    private readonly StationManager stations;
    private readonly DispatcherQueue ui;
    private readonly bool loading;
    private IDisposable? listening;
    private int refreshQueued;

    public LighthouseViewModel(AppHost host, DispatcherQueue ui)
    {
        this.host = host;
        stations = host.Stations;
        this.ui = ui;

        loading = true;
        var settings = SettingsViewModel.Read(host, out var problem);
        SettingsProblem = problem;
        PowerOnAtMonadoStart = settings.BaseStationsOnAtStart;
        PowerOffAtMonadoStop = settings.BaseStationsOffAtStop;
        PowerOnAtMiradorStart = settings.BaseStationsOnAtMiradorStart;
        PowerOffAtMiradorQuit = settings.BaseStationsOffAtMiradorQuit;
        ControllersOffAtMonadoStop = settings.ControllersOffAtStop;
        StationState = settings.BaseStationsState == StationStateMode.Live ? 0 : 1;
        loading = false;
        ReadSteamVrButtonStart();
    }

    [ObservableProperty]
    public partial bool SteamVrButtonStartStopped { get; set; }

    public bool CanStopSteamVrButtonStart => host.SteamVrSettings is not null;

    public string SteamVrButtonStartDescription => host.SteamVrSettings is null
        ? "Steam isn't installed."
        : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSteamVrButtonStartProblem))]
    public partial string? SteamVrButtonStartProblem { get; set; }

    public bool HasSteamVrButtonStartProblem => SteamVrButtonStartProblem is not null;

    public const string SteamVrButtonStartQuestion =
        "Mirador will change a SteamVR setting so a controller button no longer starts SteamVR. A copy of SteamVR's settings is kept.";

    public void ReadSteamVrButtonStart()
    {
        try
        {
            SteamVrButtonStartStopped = host.SteamVrSettings?.AutoLaunchOnButton() == false;
        }
        catch (SteamVrSettingsException e)
        {
            SteamVrButtonStartProblem = e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SteamVrButtonStartProblem = $"Mirador couldn't read SteamVR's settings ({e.Message.Trim().TrimEnd('.')}).";
        }
    }

    public string? SteamVrStillRunning()
    {
        var running = LighthouseConflicts.Find(host.Processes.Snapshot());
        return running.Any(name => name is "vrserver" or "vrmonitor") ? "Close SteamVR first." : null;
    }

    public string? SetSteamVrButtonStart(bool stop)
    {
        string? problem = null;
        try
        {
            if (host.SteamVrSettings is not { } steamVr)
            {
                problem = "Steam isn't installed.";
            }
            else if (SteamVrStillRunning() is { } running)
            {
                problem = running;
            }
            else
            {
                var settings = host.Settings.Load();
                if (stop)
                {
                    var before = settings.KeepSteamVrFromButtonStart ? settings.SteamVrButtonStartBefore : steamVr.AutoLaunchOnButton();
                    problem = SettingsViewModel.Write(host, s => s with { SteamVrButtonStartBefore = before });
                    if (problem is null)
                    {
                        steamVr.SetAutoLaunchOnButton(false);
                        problem = SettingsViewModel.Write(host, s => s with { KeepSteamVrFromButtonStart = true });
                    }
                }
                else
                {
                    steamVr.SetAutoLaunchOnButton(settings.KeepSteamVrFromButtonStart ? settings.SteamVrButtonStartBefore : null);
                    problem = SettingsViewModel.Write(host, s => s with { KeepSteamVrFromButtonStart = false, SteamVrButtonStartBefore = null });
                }
            }
        }
        catch (SteamVrSettingsException e)
        {
            problem = e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = $"Mirador couldn't change SteamVR's settings ({e.Message.Trim().TrimEnd('.')}).";
        }

        SteamVrButtonStartProblem = problem;
        ReadSteamVrButtonStart();
        App.Current.Home.CheckSteamVrButtonStart();
        return problem;
    }

    public ObservableCollection<StationItem> Stations { get; } = [];

    [ObservableProperty]
    public partial bool PowerOnAtMonadoStart { get; set; }

    [ObservableProperty]
    public partial bool PowerOffAtMonadoStop { get; set; }

    [ObservableProperty]
    public partial bool PowerOnAtMiradorStart { get; set; }

    [ObservableProperty]
    public partial bool PowerOffAtMiradorQuit { get; set; }

    [ObservableProperty]
    public partial bool ControllersOffAtMonadoStop { get; set; }

    [ObservableProperty]
    public partial int StationState { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsProblem))]
    public partial string? SettingsProblem { get; set; }

    public bool HasSettingsProblem => SettingsProblem is not null;

    [ObservableProperty]
    public partial bool BluetoothMissing { get; set; }

    [ObservableProperty]
    public partial bool BluetoothOff { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(SwitchOnCommand), nameof(SwitchOffCommand))]
    public partial bool BluetoothReady { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveProblem))]
    public partial string? SaveProblem { get; set; }

    public bool HasSaveProblem => SaveProblem is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    public partial bool IsLooking { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchOnCommand), nameof(SwitchOffCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchOnCommand), nameof(SwitchOffCommand))]
    public partial bool AnySelected { get; set; }

    [ObservableProperty]
    public partial bool HasNoStations { get; set; }

    public void SetShown(bool shown)
    {
        if (shown == (listening is not null))
        {
            return;
        }

        if (shown)
        {
            stations.Changed += OnChanged;
            host.Session.Changed += OnSessionChanged;
            listening = stations.Listen();
            Refresh();
            RefreshRoomSetup();
            _ = CheckRoomSetupAsync();
        }
        else
        {
            stations.Changed -= OnChanged;
            host.Session.Changed -= OnSessionChanged;
            listening!.Dispose();
            listening = null;
        }
    }

    public const string RoomSetupRestartNote = "Monado stops when the measuring program closes; Mirador starts it again.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoomSetupUnavailable))]
    [NotifyCanExecuteChangedFor(nameof(MeasureFloorCommand), nameof(SetForwardCommand))]
    public partial string? RoomSetupUnavailable { get; set; }

    public bool HasRoomSetupUnavailable => RoomSetupUnavailable is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingRoomSetup))]
    public partial string? MissingRoomSetup { get; set; }

    public bool HasMissingRoomSetup => MissingRoomSetup is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChangedRoomSetup))]
    public partial string? ChangedRoomSetup { get; set; }

    public bool HasChangedRoomSetup => ChangedRoomSetup is not null;

    private async Task CheckRoomSetupAsync()
    {
        (MissingRoomSetup, ChangedRoomSetup) = await Task.Run(host.RoomSetupWarnings);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MeasureFloorCommand), nameof(SetForwardCommand))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    public partial bool RoomSetupBusy { get; set; }

    public bool CanExport => !RoomSetupBusy;

    public async Task<string?> PrepareExportAsync()
    {
        var (refusal, question, _, _) = await Task.Run(CheckExport);
        if (refusal is not null)
        {
            ShowRoomSetup(refusal, InfoBarSeverity.Warning);
        }

        return question;
    }

    public async Task ExportAsync()
    {
        RoomSetupBusy = true;
        try
        {
            var refusal = await host.Session.RunWhileStoppedAsync(
                "",
                (_, _) => Task.Run(() =>
                {
                    var check = CheckExport();
                    if (check.Refusal is null)
                    {
                        RoomSetupExport.Run(
                            check.Steam!, Path.Combine(host.Paths.BackupsDir, "steamvr"), check.Universe!, host.RoomSetups, TimeProvider.System);
                    }

                    return check.Refusal;
                }),
                CancellationToken.None);
            ShowRoomSetup(
                refusal ?? "The room setup is in SteamVR's settings now, and Mirador applies none of its own in this space. Monado uses it from its next start.",
                refusal is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (MiradorException e)
        {
            ShowRoomSetup(e.Message, InfoBarSeverity.Error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowRoomSetup($"Mirador couldn't write SteamVR's room setup ({e.Message.Trim().TrimEnd('.')}). Nothing changed.", InfoBarSeverity.Error);
        }
        catch (Exception e)
        {
            AppLog.Write("Exporting the room setup failed", e);
            ShowRoomSetup($"Mirador couldn't export the room setup: {e.Message.Trim().TrimEnd('.')}.", InfoBarSeverity.Error);
        }
        finally
        {
            RoomSetupBusy = false;
            _ = CheckRoomSetupAsync();
            App.Current.Home.CheckRoomSetup();
        }
    }

    private (string? Refusal, string? Question, string? Steam, string? Universe) CheckExport()
    {
        var steam = AppHost.SteamDir();
        string? universe;
        try
        {
            universe = SteamVrNow.Read(steam, host.KnownStations()).Pick.Id;
        }
        catch (MiradorException e)
        {
            return (e.Message, null, steam, null);
        }

        var monadoRunning = host.Session.State != SessionState.Stopped
            || host.Processes.Snapshot().Any(p => string.Equals(p.Name, "monado-service", StringComparison.OrdinalIgnoreCase));
        var refusal = RoomSetupExport.Refusal(SteamVrStillRunning() is not null, monadoRunning, steam, universe, host.RoomSetups);
        return refusal is null ? (null, RoomSetupExport.QuestionFor(steam!, universe!), steam, universe) : (refusal, null, steam, universe);
    }

    [ObservableProperty]
    public partial bool RoomSetupStopsMonado { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoomSetupMessage))]
    public partial string? RoomSetupMessage { get; set; }

    public bool HasRoomSetupMessage => RoomSetupMessage is not null;

    [ObservableProperty]
    public partial InfoBarSeverity RoomSetupSeverity { get; set; }

    [RelayCommand(CanExecute = nameof(CanMeasure))]
    private Task MeasureFloor() => RunRoomStepAsync(RoomStep.Floor);

    [RelayCommand(CanExecute = nameof(CanMeasure))]
    private Task SetForward() => RunRoomStepAsync(RoomStep.Forward);

    private bool CanMeasure() => RoomSetupUnavailable is null && !RoomSetupBusy;

    private void OnSessionChanged() => ui.TryEnqueue(() =>
    {
        if (listening is not null)
        {
            RefreshRoomSetup();
        }
    });

    private void RefreshRoomSetup()
    {
        var session = host.Session;
        var running = session.State == SessionState.Running;
        RoomSetupStopsMonado = running && RoomMeasuring.StopsMonado(session.LastMiradorEnvironment);
        RoomSetupUnavailable = session.Monado switch
        {
            { OffersOriginOffsets: true } => null,
            { } => Libmonado.NotOffered,
            null when running && session.MonadoClientProblem is { } problem => problem,
            null when running => "Waiting for Monado's libmonado connection.",
            null => "Start Monado first.",
        };
    }

    private async Task RunRoomStepAsync(RoomStep step)
    {
        if (host.Session.Monado is null)
        {
            RefreshRoomSetup();
            return;
        }

        RoomSetupBusy = true;
        try
        {
            using var session = new SessionForRoomSetup(host);
            var run = new RoomSetupRun(host.RoomMeasuring, host.RoomSetups, session, TimeProvider.System)
            {
                Log = AppLog.Write,
            };
            void Status(string text) => ui.TryEnqueue(() => ShowRoomSetup(text, InfoBarSeverity.Informational));
            var outcome = await Task.Run(() => run.RunAsync(step, Status, CancellationToken.None));
            ShowRoomSetup(outcome.Message, outcome.Level switch
            {
                RoomStepLevel.Success => InfoBarSeverity.Success,
                RoomStepLevel.Warning => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Error,
            });
        }
        catch (Exception e)
        {
            AppLog.Write("The room setup failed", e);
            ShowRoomSetup($"Mirador couldn't set up the room: {e.Message.Trim().TrimEnd('.')}.", InfoBarSeverity.Error);
        }
        finally
        {
            RoomSetupBusy = false;
            RefreshRoomSetup();
            _ = CheckRoomSetupAsync();
            App.Current.Home.CheckRoomSetup();
        }
    }

    private sealed class SessionForRoomSetup : IRoomSetupSession, IDisposable
    {
        private readonly AppHost host;
        private volatile SessionEnd? lastEnd;

        public SessionForRoomSetup(AppHost host)
        {
            this.host = host;
            host.Session.Ended += OnEnded;
        }

        public SessionEnd? LastEnd => lastEnd;

        public bool Quitting => App.Current.Quitting;

        public IMonadoClient? MonadoClient => host.Session.Monado;

        public string? RuntimeManifest => host.Session.CurrentInstall?.Manifest;

        public IReadOnlyDictionary<string, string>? MonadoEnvironment => host.Session.LastMiradorEnvironment;

        public SteamVrNow? SteamVr() => host.RunUsesBaseStations() ? SteamVrNow.Read(AppHost.SteamDir(), host.KnownStations()) : null;

        public Task<StartOutcome> StartMonadoAsync() => App.Current.StartAsync(StartReason.Button);

        public void Dispose() => host.Session.Ended -= OnEnded;

        private void OnEnded(SessionEnd end) => lastEnd = end;
    }

    private void ShowRoomSetup(string message, InfoBarSeverity severity)
    {
        RoomSetupMessage = message;
        RoomSetupSeverity = severity;
    }

    public string? Rename(StationItem item, string text) => stations.Rename(item.Address, text);

    public string? SetId(StationItem item, string text) => stations.SetId(item.Address, text);

    internal void SetSelected(StationItem item, bool selected) => stations.SetSelected(item.Address, selected);

    internal void Switch(StationItem item, StationCommand command) =>
        _ = stations.SwitchAsync(item.Address, command, CancellationToken.None);

    internal void SwitchOff(StationItem item) => Switch(item, stations.OffCommand());

    internal void Identify(StationItem item) => _ = stations.IdentifyAsync(item.Address, CancellationToken.None);

    internal void Forget(StationItem item) => stations.Forget(item.Address);

    [RelayCommand(CanExecute = nameof(CanScan))]
    private void Scan() => _ = stations.ScanAsync(CancellationToken.None);

    private bool CanScan() => BluetoothReady && !IsLooking;

    [RelayCommand(CanExecute = nameof(CanSwitchSelected))]
    private void SwitchOn() => _ = stations.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);

    [RelayCommand(CanExecute = nameof(CanSwitchSelected))]
    private void SwitchOff() => _ = stations.SwitchSelectedAsync(stations.OffCommand(), CancellationToken.None);

    private bool CanSwitchSelected() => BluetoothReady && AnySelected && !IsRunning;

    partial void OnPowerOnAtMonadoStartChanged(bool value) => SaveSetting(s => s with { BaseStationsOnAtStart = value });

    partial void OnPowerOffAtMonadoStopChanged(bool value) => SaveSetting(s => s with { BaseStationsOffAtStop = value });

    partial void OnPowerOnAtMiradorStartChanged(bool value) => SaveSetting(s => s with { BaseStationsOnAtMiradorStart = value });

    partial void OnPowerOffAtMiradorQuitChanged(bool value) => SaveSetting(s => s with { BaseStationsOffAtMiradorQuit = value });

    partial void OnControllersOffAtMonadoStopChanged(bool value) => SaveSetting(s => s with { ControllersOffAtStop = value });

    partial void OnStationStateChanged(int value) =>
        SaveSetting(s => s with { BaseStationsState = value == 0 ? StationStateMode.Live : StationStateMode.LastScan });

    private void SaveSetting(Func<AppSettings, AppSettings> change)
    {
        if (loading)
        {
            return;
        }

        SettingsProblem = SettingsViewModel.Write(host, change);
        stations.SettingsChanged();
    }

    private void OnChanged()
    {
        if (Interlocked.Exchange(ref refreshQueued, 1) == 0 && !ui.TryEnqueue(RefreshQueued))
        {
            Volatile.Write(ref refreshQueued, 0);
        }
    }

    private void RefreshQueued()
    {
        Volatile.Write(ref refreshQueued, 0);
        if (listening is not null)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        var bluetooth = stations.Bluetooth;
        BluetoothMissing = bluetooth == BluetoothState.Missing;
        BluetoothOff = bluetooth == BluetoothState.Off;
        BluetoothReady = bluetooth == BluetoothState.Ready;
        SaveProblem = stations.SaveProblem;
        IsLooking = stations.Looking;
        IsRunning = stations.Running is not null;

        var views = stations.Stations;
        AnySelected = views.Any(v => v.Station.Selected);
        Show(views);
        HasNoStations = views.Count == 0;
    }

    private void Show(IReadOnlyList<StationView> views)
    {
        var addresses = views.Select(v => v.Station.Address).ToHashSet(StringComparer.Ordinal);
        for (var i = Stations.Count - 1; i >= 0; i--)
        {
            if (!addresses.Contains(Stations[i].Address))
            {
                Stations.RemoveAt(i);
            }
        }

        for (var i = 0; i < views.Count; i++)
        {
            var address = views[i].Station.Address;
            var at = IndexOf(address, i);
            StationItem item;
            if (at < 0)
            {
                item = new StationItem(this, address);
                Stations.Insert(i, item);
            }
            else
            {
                item = Stations[at];
                if (at != i)
                {
                    Stations.RemoveAt(at);
                    Stations.Insert(i, item);
                }
            }

            item.Update(views[i], IsLooking, BluetoothReady);
        }
    }

    private int IndexOf(string address, int from)
    {
        for (var i = from; i < Stations.Count; i++)
        {
            if (Stations[i].Address == address)
            {
                return i;
            }
        }

        return -1;
    }
}
