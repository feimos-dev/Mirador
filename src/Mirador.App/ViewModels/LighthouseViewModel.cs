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

/// <summary>One base station on the Lighthouse page: what its card shows, and what its buttons and menu do.</summary>
public sealed partial class StationItem : ObservableObject
{
    private readonly LighthouseViewModel owner;
    private bool updating;

    public StationItem(LighthouseViewModel owner, string address)
    {
        this.owner = owner;
        Address = address;
    }

    /// <summary>The Bluetooth address, which names the station to the manager.</summary>
    public string Address { get; }

    /// <summary>The station's own Bluetooth name ("LHB-…", "HTC BS …"): the rename dialog's placeholder.</summary>
    public string Name { get; private set; } = "";

    /// <summary>The name the user gave it, or null.</summary>
    public string? CustomName { get; private set; }

    /// <summary>A 1.0 station's ID, or null.</summary>
    public string? Id { get; private set; }

    [ObservableProperty]
    public partial string DisplayName { get; set; } = "";

    /// <summary>The address, after the station's own name when the user renamed it.</summary>
    [ObservableProperty]
    public partial string AddressLine { get; set; } = "";

    /// <summary>What it is doing: "2.0 · On", "Not seen in the last scan", "Looking…" (during a scan), "Didn't answer", …</summary>
    [ObservableProperty]
    public partial string StateLine { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsV2 { get; set; }

    public bool IsV1 => !IsV2;

    /// <summary>Whether Identify can be sent: a 2.0 station that is on, and Bluetooth ready.</summary>
    [ObservableProperty]
    public partial bool CanIdentify { get; set; }

    /// <summary>For screen readers, why Identify is greyed (its tooltip is "Identify" either way); empty while the station is on.</summary>
    [ObservableProperty]
    public partial string IdentifyHelp { get; set; } = "";

    /// <summary>Whether its menu's Power on and Power off can be sent (Bluetooth is ready).</summary>
    public bool CanSwitch { get; private set; }

    /// <summary>The station blinks (Identify).</summary>
    public void Identify() => owner.Identify(this);

    public void PowerOn() => owner.Switch(this, StationCommand.On);

    /// <summary>Power off: the station sleeps.</summary>
    public void PowerOff() => owner.SwitchOff(this);

    public void Forget() => owner.Forget(this);

    /// <summary>Takes what the manager says about the station now.</summary>
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

/// <summary>
/// The Lighthouse page: Bluetooth's state, the known base stations with their states, switching them, and the base
/// stations' settings. It follows the manager's changes while it is shown (<see cref="SetShown"/>), and holds a listen
/// then (the manager decides whether to listen, by "Station state tracking rate"); commands go on when the
/// page is left. The settings save at once, as App settings do.
/// </summary>
public sealed partial class LighthouseViewModel : ObservableObject
{
    private readonly AppHost host;
    private readonly StationManager stations;
    private readonly DispatcherQueue ui;
    private readonly bool loading;
    private IDisposable? listening;
    private int refreshQueued;

    /// <param name="host">Mirador's parts; the page uses its <see cref="AppHost.Stations"/> and <see cref="AppHost.Settings"/>.</param>
    /// <param name="ui">The page's queue: every change the manager reports is shown through it.</param>
    public LighthouseViewModel(AppHost host, DispatcherQueue ui)
    {
        this.host = host;
        stations = host.Stations;
        this.ui = ui;

        // Read once: the base stations' settings are changed only here.
        loading = true;
        var settings = SettingsViewModel.Read(host, out var problem);
        SettingsProblem = problem;
        PowerOnAtMonadoStart = settings.BaseStationsOnAtStart;
        PowerOffAtMonadoStop = settings.BaseStationsOffAtStop;
        PowerOnAtMiradorStart = settings.BaseStationsOnAtMiradorStart;
        PowerOffAtMiradorQuit = settings.BaseStationsOffAtMiradorQuit;
        ControllersOffAtMonadoStop = settings.ControllersOffAtStop;
        StationState = settings.BaseStationsState == StationStateMode.Live ? 0 : 1; // Constant first, When Relevant second
        loading = false;
        ReadSteamVrButtonStart();
    }

    /// <summary>
    /// "Stop SteamVR starting when a controller turns on": on when SteamVR's own setting says <c>false</c>, whoever wrote
    /// it (Mirador, or the user by hand).
    /// </summary>
    [ObservableProperty]
    public partial bool SteamVrButtonStartStopped { get; set; }

    /// <summary>Whether the switch can be used: Steam is installed.</summary>
    public bool CanStopSteamVrButtonStart => host.SteamVrSettings is not null;

    /// <summary>The switch's description: what it does, or why it can't.</summary>
    public string SteamVrButtonStartDescription => host.SteamVrSettings is null
        ? "Steam isn't installed."
        : "SteamVR then doesn't start when a controller or tracker is switched on. Mirador changes SteamVR's own setting, and puts it back when you turn this off.";

    /// <summary>Why the switch couldn't be changed, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSteamVrButtonStartProblem))]
    public partial string? SteamVrButtonStartProblem { get; set; }

    public bool HasSteamVrButtonStartProblem => SteamVrButtonStartProblem is not null;

    /// <summary>The question asked before Mirador first changes SteamVR's setting.</summary>
    public const string SteamVrButtonStartQuestion =
        "Mirador will change a SteamVR setting so a controller button no longer starts SteamVR. A copy of SteamVR's settings is kept.";

    /// <summary>Reads the switch's state from SteamVR's settings again.</summary>
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

    /// <summary>"Close SteamVR first." while SteamVR runs (it rewrites its settings when it exits); null when it doesn't.</summary>
    public string? SteamVrStillRunning()
    {
        var running = LighthouseConflicts.Find(host.Processes.Snapshot());
        return running.Any(name => name is "vrserver" or "vrmonitor") ? "Close SteamVR first." : null;
    }

    /// <summary>
    /// Turns the switch on (the page asked first) or off, while SteamVR is closed. On: SteamVR's setting as it is
    /// now is kept (once, at Mirador's first write), then <c>false</c> is written. Off: that kept setting goes back (none:
    /// the key goes). Null when done, else what went wrong; the switch then shows SteamVR's setting as it is.
    /// </summary>
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

    /// <summary>"Power on when Monado starts".</summary>
    [ObservableProperty]
    public partial bool PowerOnAtMonadoStart { get; set; }

    /// <summary>"Power off when Monado stops".</summary>
    [ObservableProperty]
    public partial bool PowerOffAtMonadoStop { get; set; }

    /// <summary>"Power on when Mirador starts".</summary>
    [ObservableProperty]
    public partial bool PowerOnAtMiradorStart { get; set; }

    /// <summary>"Power off when Mirador quits".</summary>
    [ObservableProperty]
    public partial bool PowerOffAtMiradorQuit { get; set; }

    /// <summary>"Turn off controllers and trackers when Monado stops".</summary>
    [ObservableProperty]
    public partial bool ControllersOffAtMonadoStop { get; set; }

    /// <summary>The "Station state" drop-down: 0 from the last scan, 1 live while shown.</summary>
    [ObservableProperty]
    public partial int StationState { get; set; }

    /// <summary>Why the settings couldn't be read or saved, or null.</summary>
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

    /// <summary>Why the list couldn't be saved, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveProblem))]
    public partial string? SaveProblem { get; set; }

    public bool HasSaveProblem => SaveProblem is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    public partial bool IsLooking { get; set; }

    /// <summary>Whether a command is being sent to the selected stations now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchOnCommand), nameof(SwitchOffCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchOnCommand), nameof(SwitchOffCommand))]
    public partial bool AnySelected { get; set; }

    [ObservableProperty]
    public partial bool HasNoStations { get; set; }

    /// <summary>
    /// Follows the stations' changes, and holds a listen, while <paramref name="shown"/>: the page is the current one
    /// and the window is on screen. Stops otherwise.
    /// </summary>
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

    /// <summary>What the card says first when the measuring program's exit stops Monado ("exit on disconnect").</summary>
    public const string RoomSetupRestartNote = "Monado stops when the measuring program closes; Mirador starts it again.";

    /// <summary>Why the room setup's steps can't be used now ("Start Monado first.", no libmonado); null when they can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoomSetupUnavailable))]
    [NotifyCanExecuteChangedFor(nameof(MeasureFloorCommand), nameof(SetForwardCommand))]
    public partial string? RoomSetupUnavailable { get; set; }

    public bool HasRoomSetupUnavailable => RoomSetupUnavailable is not null;

    /// <summary>The space in use has no room setup (<see cref="AppHost.RoomSetupWarnings"/>); null when it has one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingRoomSetup))]
    public partial string? MissingRoomSetup { get; set; }

    public bool HasMissingRoomSetup => MissingRoomSetup is not null;

    /// <summary>SteamVR's room setup changed since Mirador's was measured on it; null when it didn't.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChangedRoomSetup))]
    public partial string? ChangedRoomSetup { get; set; }

    public bool HasChangedRoomSetup => ChangedRoomSetup is not null;

    /// <summary>Reads the room setup's warnings, for this page, off the UI thread.</summary>
    private async Task CheckRoomSetupAsync()
    {
        (MissingRoomSetup, ChangedRoomSetup) = await Task.Run(host.RoomSetupWarnings);
    }

    /// <summary>A step is measuring, or an export writing, now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MeasureFloorCommand), nameof(SetForwardCommand))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    public partial bool RoomSetupBusy { get; set; }

    /// <summary>Whether "Export to SteamVR" can be pressed: nothing else of the room setup runs.</summary>
    public bool CanExport => !RoomSetupBusy;

    /// <summary>
    /// Whether "Export to SteamVR" can go ahead: the question the page then asks first, or null when it can't (why not
    /// is shown). Reads files off the UI thread.
    /// </summary>
    public async Task<string?> PrepareExportAsync()
    {
        var (refusal, question) = await Task.Run(CheckExport);
        if (refusal is not null)
        {
            ShowRoomSetup(refusal, InfoBarSeverity.Warning);
        }

        return question;
    }

    /// <summary>
    /// Writes the room setup that applies in the space in use into SteamVR's (a copy kept, written aside and moved
    /// over), then keeps none of Mirador's own for the space, so that never both apply. Asked first by the page;
    /// checked again here.
    /// </summary>
    public async Task ExportAsync()
    {
        RoomSetupBusy = true;
        try
        {
            var refusal = await Task.Run(() =>
            {
                var (why, _) = CheckExport();
                if (why is null)
                {
                    RoomSetupExport.Run(
                        AppHost.SteamDir()!,
                        Path.Combine(host.Paths.BackupsDir, "steamvr"),
                        host.CurrentUniverse().Id!,
                        host.RoomSetups,
                        TimeProvider.System);
                }

                return why;
            });
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

    /// <summary>
    /// Why the export can't go ahead (<see cref="RoomSetupExport.Refusal"/>: SteamVR running, then Monado running, …),
    /// or the question to ask. Reads files and processes: not on the UI thread.
    /// </summary>
    private (string? Refusal, string? Question) CheckExport()
    {
        var steam = AppHost.SteamDir();
        var universe = steam is null ? null : host.CurrentUniverse().Id;
        // Monado started by Mirador, or any monado-service (one started outside Mirador too).
        var monadoRunning = host.Session.State != SessionState.Stopped
            || host.Processes.Snapshot().Any(p => string.Equals(p.Name, "monado-service", StringComparison.OrdinalIgnoreCase));
        var refusal = RoomSetupExport.Refusal(SteamVrStillRunning() is not null, monadoRunning, steam, universe, host.RoomSetups);
        return refusal is null ? (null, RoomSetupExport.QuestionFor(steam!, universe!)) : (refusal, null);
    }

    /// <summary>Monado runs with "exit on disconnect": the card says what happens first.</summary>
    [ObservableProperty]
    public partial bool RoomSetupStopsMonado { get; set; }

    /// <summary>How the last step went, or what it is doing; null before the first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoomSetupMessage))]
    public partial string? RoomSetupMessage { get; set; }

    public bool HasRoomSetupMessage => RoomSetupMessage is not null;

    [ObservableProperty]
    public partial InfoBarSeverity RoomSetupSeverity { get; set; }

    /// <summary>"Lay a controller on the floor, then Measure floor": the lower grip is the floor.</summary>
    [RelayCommand(CanExecute = nameof(CanMeasure))]
    private Task MeasureFloor() => RunRoomStepAsync(RoomStep.Floor);

    /// <summary>"Look straight ahead where forward should be, then Set forward": the head's direction becomes forward.</summary>
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

    /// <summary>Whether the steps can be used now, and whether Monado stops with the measuring program.</summary>
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

    /// <summary>
    /// One step (<see cref="RoomSetupRun"/>), off the UI thread: measures, keeps the new offsets for the current Lighthouse
    /// universe and sets them; when the measuring program's exit stopped Monado ("exit on disconnect"), Monado is started
    /// again, also after a failure.
    /// </summary>
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

    /// <summary>The session as a room setup step uses it; Monado is started again as the Start button does.</summary>
    private sealed class SessionForRoomSetup : IRoomSetupSession, IDisposable
    {
        private readonly AppHost host;
        private volatile SessionEnd? lastEnd;

        /// <summary>Follows how Monado's run ends from now on, until disposed.</summary>
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

        public SteamVrNow SteamVr() => SteamVrNow.Read(AppHost.SteamDir(), host.KnownStations());

        public Task<StartOutcome> StartMonadoAsync() => App.Current.StartAsync(StartReason.Button);

        public void Dispose() => host.Session.Ended -= OnEnded;

        private void OnEnded(SessionEnd end) => lastEnd = end;
    }

    private void ShowRoomSetup(string message, InfoBarSeverity severity)
    {
        RoomSetupMessage = message;
        RoomSetupSeverity = severity;
    }

    /// <summary>Gives the station a name of the user's; null when saved, else what went wrong (the dialog stays open).</summary>
    public string? Rename(StationItem item, string text) => stations.Rename(item.Address, text);

    /// <summary>Keeps a 1.0 station's ID; null when saved, else what went wrong (the dialog stays open).</summary>
    public string? SetId(StationItem item, string text) => stations.SetId(item.Address, text);

    internal void SetSelected(StationItem item, bool selected) => stations.SetSelected(item.Address, selected);

    internal void Switch(StationItem item, StationCommand command) =>
        _ = stations.SwitchAsync(item.Address, command, CancellationToken.None);

    internal void SwitchOff(StationItem item) => Switch(item, stations.OffCommand());

    internal void Identify(StationItem item) => _ = stations.IdentifyAsync(item.Address, CancellationToken.None);

    internal void Forget(StationItem item) => stations.Forget(item.Address);

    /// <summary>Scans for the base stations (8 s); opening the page doesn't.</summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private void Scan() => _ = stations.ScanAsync(CancellationToken.None);

    private bool CanScan() => BluetoothReady && !IsLooking;

    [RelayCommand(CanExecute = nameof(CanSwitchSelected))]
    private void SwitchOn() => _ = stations.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);

    /// <summary>Off: the selected stations sleep.</summary>
    [RelayCommand(CanExecute = nameof(CanSwitchSelected))]
    private void SwitchOff() => _ = stations.SwitchSelectedAsync(stations.OffCommand(), CancellationToken.None);

    /// <summary>On and Off: Bluetooth is ready, a station is selected and no command is being sent.</summary>
    private bool CanSwitchSelected() => BluetoothReady && AnySelected && !IsRunning;

    partial void OnPowerOnAtMonadoStartChanged(bool value) => SaveSetting(s => s with { BaseStationsOnAtStart = value });

    partial void OnPowerOffAtMonadoStopChanged(bool value) => SaveSetting(s => s with { BaseStationsOffAtStop = value });

    partial void OnPowerOnAtMiradorStartChanged(bool value) => SaveSetting(s => s with { BaseStationsOnAtMiradorStart = value });

    partial void OnPowerOffAtMiradorQuitChanged(bool value) => SaveSetting(s => s with { BaseStationsOffAtMiradorQuit = value });

    partial void OnControllersOffAtMonadoStopChanged(bool value) => SaveSetting(s => s with { ControllersOffAtStop = value });

    partial void OnStationStateChanged(int value) =>
        SaveSetting(s => s with { BaseStationsState = value == 0 ? StationStateMode.Live : StationStateMode.LastScan });

    /// <summary>Saves one setting at once and tells the manager, which reads "Station state" again.</summary>
    private void SaveSetting(Func<AppSettings, AppSettings> change)
    {
        if (loading)
        {
            return;
        }

        SettingsProblem = SettingsViewModel.Write(host, change);
        stations.SettingsChanged();
    }

    /// <summary>On whichever thread the manager is on: shows the change on the page's thread, once for a burst of them.</summary>
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

    /// <summary>Reads everything the page shows from the manager again; the cards are updated in place.</summary>
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

    /// <summary>
    /// Puts the stations in the list in the manager's order, keeping each one's card (so that an open menu or the
    /// focus survives an update): cards of stations no longer listed go, new ones come in.
    /// </summary>
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
