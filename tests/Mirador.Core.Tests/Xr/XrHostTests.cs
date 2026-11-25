using Microsoft.Extensions.Time.Testing;
using Mirador.Core.Monado;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Xr;
using Mirador.Ipc;

namespace Mirador.Core.Tests.Xr;

public sealed class XrHostTests : IDisposable
{
    private const string Manifest = @"C:\monado\openxr_monado.json";

    private static readonly MonadoApp Home = new(1, XrLink.HomeAppName, Shown: true, SessionRunning: true, Overlay: false, InputsBlocked: false);
    private static readonly MonadoApp Bar = new(2, XrLink.DashboardAppName, Shown: false, SessionRunning: true, Overlay: true, InputsBlocked: false);
    private static readonly MonadoApp Game = new(3, "Game", Shown: true, SessionRunning: true, Overlay: false, InputsBlocked: false);

    private readonly TempDir dir = new();
    private readonly FakeTimeProvider time = new();
    private readonly Launcher launcher = new();
    private readonly Client client = new();
    private readonly Exit gameExit = new();
    private readonly Program home = new();
    private readonly Program bar = new();
    private readonly List<string> log = [];
    private readonly Record record = new();
    private bool homeOn = true;
    private bool dashboardOn = true;
    private int stopsAsked;

    public void Dispose() => dir.Dispose();

    private XrHost Host()
    {
        var exe = dir.File("Mirador.Xr.exe");
        File.WriteAllText(exe, "");
        var host = new XrHost(new XrHostDeps(
            launcher, exe, new Dictionary<string, string> { ["PATH"] = "x" }, time, () => homeOn, () => dashboardOn, log.Add, gameExit, record));
        host.StopMonadoRequested += () => stopsAsked++;
        return host;
    }

    private XrHost Running()
    {
        var host = Host();
        host.MonadoStarted(client, Manifest);
        host.Connected(XrLink.HomeRole, home);
        host.Connected(XrLink.DashboardRole, bar);
        host.Line(XrLink.HomeRole, XrLink.Running);
        host.Line(XrLink.DashboardRole, XrLink.Running);
        bar.Lines.Clear();
        return host;
    }

    private XrHost OpenAtHome()
    {
        client.AppList = [Home, Bar];
        var host = Running();
        host.Line(XrLink.HomeRole, "SHOWN 1");
        time.Advance(XrHost.OpenDelay);
        Assert.Contains("OPEN 1", bar.Lines);
        bar.Lines.Clear();
        return host;
    }

    private XrHost InGame()
    {
        client.AppList = [Home with { Shown = false }, Bar, Game];
        var host = Running();
        host.Line(XrLink.HomeRole, "SHOWN 0");
        bar.Lines.Clear();
        return host;
    }

    [Fact]
    public void Both_programs_start_with_the_running_monado_as_their_runtime()
    {
        Host().MonadoStarted(client, Manifest);

        Assert.Equal(["--home", "--dashboard"], launcher.Specs.Select(s => s.Arguments));
        Assert.All(launcher.Specs, s =>
        {
            Assert.Equal(Manifest, s.Environment["XR_RUNTIME_JSON"]);
            Assert.Equal("x", s.Environment["PATH"]);
            Assert.Equal(dir.Path, s.WorkingDirectory);
        });
    }

    [Fact]
    public void A_libmonado_without_the_app_list_starts_neither()
    {
        client.Apps = false;

        Host().MonadoStarted(client, Manifest);

        Assert.Empty(launcher.Specs);
    }

    [Fact]
    public void A_program_switched_off_is_not_started()
    {
        homeOn = false;

        Host().MonadoStarted(client, Manifest);

        Assert.Equal(["--dashboard"], launcher.Specs.Select(s => s.Arguments));
    }

    [Fact]
    public void A_home_that_begins_over_a_game_hands_the_view_back()
    {
        client.AppList = [Game with { Shown = false }, Home, Bar];
        var host = Host();
        host.MonadoStarted(client, Manifest);
        host.Connected(XrLink.HomeRole, home);

        host.Line(XrLink.HomeRole, XrLink.Running);

        Assert.Equal([Game.Id], client.ShownIds);
    }

    [Fact]
    public void A_home_that_begins_alone_keeps_the_view()
    {
        client.AppList = [Home, Bar];
        var host = Host();
        host.MonadoStarted(client, Manifest);
        host.Connected(XrLink.HomeRole, home);

        host.Line(XrLink.HomeRole, XrLink.Running);

        Assert.Empty(client.ShownIds);
    }

    [Fact]
    public void A_home_under_a_game_that_began_later_takes_nothing()
    {
        client.AppList = [Home with { Shown = false }, Game];
        var host = Host();
        host.MonadoStarted(client, Manifest);
        host.Connected(XrLink.HomeRole, home);

        host.Line(XrLink.HomeRole, XrLink.Running);

        Assert.Empty(client.ShownIds);
    }

    [Fact]
    public void The_dashboard_opens_once_the_home_has_stayed_shown()
    {
        client.AppList = [Home, Bar];
        client.DeviceList = [new MonadoDevice(1, "left", "Index L", "LHR-AAAA1111", 0, true, false, 0.82f)];
        var host = Running();

        host.Line(XrLink.HomeRole, "SHOWN 1");
        Assert.Empty(bar.Lines);
        time.Advance(XrHost.OpenDelay);

        Assert.Equal(["EXITS monado", "DEVICES L,82,0", "OPEN 1"], bar.Lines);
        Assert.Empty(client.Blocks);
    }

    [Fact]
    public void A_game_that_takes_the_view_at_once_never_sees_the_dashboard()
    {
        client.AppList = [Home, Bar];
        var host = Running();

        host.Line(XrLink.HomeRole, "SHOWN 1");
        host.Line(XrLink.HomeRole, "SHOWN 0");
        time.Advance(XrHost.OpenDelay + XrHost.OpenDelay);

        Assert.DoesNotContain("OPEN 1", bar.Lines);
    }

    [Fact]
    public void A_game_taking_the_view_closes_the_dashboard()
    {
        var host = OpenAtHome();

        host.Line(XrLink.HomeRole, "SHOWN 0");

        Assert.Equal(["OPEN 0"], bar.Lines);
    }

    [Fact]
    public void The_system_button_opens_the_dashboard_over_a_game_and_blocks_its_inputs()
    {
        var host = InGame();

        host.Line(XrLink.DashboardRole, XrLink.Toggle);

        Assert.Equal([(Game.Id, true)], client.Blocks);
        Assert.Equal(["EXITS nothing", "DEVICES", "OPEN 1"], bar.Lines);
    }

    [Fact]
    public void Closing_the_dashboard_gives_the_game_its_inputs_back()
    {
        var host = InGame();
        host.Line(XrLink.DashboardRole, XrLink.Toggle);

        host.Line(XrLink.DashboardRole, XrLink.Toggle);

        Assert.Equal([(Game.Id, true), (Game.Id, false)], client.Blocks);
        Assert.Equal("OPEN 0", bar.Lines[^1]);
    }

    [Fact]
    public void A_dashboard_that_goes_away_open_gives_the_game_its_inputs_back()
    {
        var host = InGame();
        host.Line(XrLink.DashboardRole, XrLink.Toggle);

        host.Disconnected(XrLink.DashboardRole, bar);

        Assert.Equal([(Game.Id, true), (Game.Id, false)], client.Blocks);
    }

    [Fact]
    public void At_home_the_system_button_closes_and_opens_the_dashboard_without_blocking()
    {
        var host = OpenAtHome();

        host.Line(XrLink.DashboardRole, XrLink.Toggle);
        host.Line(XrLink.DashboardRole, XrLink.Toggle);

        Assert.Equal(["OPEN 0", "EXITS monado", "DEVICES", "OPEN 1"], bar.Lines);
        Assert.Empty(client.Blocks);
    }

    [Fact]
    public void A_game_that_can_be_ended_is_offered_to_the_exit_button_and_ended_by_it()
    {
        gameExit.Can = true;
        var host = InGame();
        host.Line(XrLink.DashboardRole, XrLink.Toggle);
        Assert.Contains("EXITS game", bar.Lines);

        host.Line(XrLink.DashboardRole, XrLink.Exit);

        Assert.Equal(1, gameExit.Quits);
        Assert.Equal(0, stopsAsked);
    }

    [Fact]
    public void At_home_the_exit_button_asks_for_monado_to_stop()
    {
        var host = OpenAtHome();

        host.Line(XrLink.DashboardRole, XrLink.Exit);

        Assert.Equal(1, stopsAsked);
        Assert.Equal(0, gameExit.Quits);
    }

    [Fact]
    public void A_recentre_moves_the_stage_onto_the_floor_under_the_head()
    {
        client.Stage = new MonadoPose(0, 0, 0, 1, 2, 0, 3);
        var host = OpenAtHome();

        host.Line(XrLink.DashboardRole, XrLink.FormatRecentre([0, 0, 0, 1, 1, 1.7f, 1]));

        Assert.Equal(new MonadoPose(0, 0, 0, 1, 3, 0, 4), client.Stage);
        Assert.Equal(1, client.LocalRecentres);
        Assert.Equal(["RECENTRED 1"], bar.Lines);
    }

    [Fact]
    public void A_recentre_this_monado_cannot_do_is_answered_with_no()
    {
        client.StageOffsets = false;
        client.LocalRecentre = false;
        var host = OpenAtHome();

        host.Line(XrLink.DashboardRole, XrLink.FormatRecentre([0, 0, 0, 1, 1, 1.7f, 1]));

        Assert.Equal(["RECENTRED 0"], bar.Lines);
    }

    [Fact]
    public void The_open_dashboard_gets_the_devices_again_every_few_seconds()
    {
        OpenAtHome();
        client.DeviceList = [new MonadoDevice(2, "right", "Index R", "LHR-BBBB2222", 0, true, true, 0.5f)];

        time.Advance(XrHost.DevicesInterval);

        Assert.Equal(["DEVICES R,50,1"], bar.Lines);
    }

    [Fact]
    public void A_program_that_exits_is_started_again()
    {
        var host = Host();
        host.MonadoStarted(client, Manifest);
        var first = launcher.Processes[0];

        first.Exit(1);
        WaitFor(() => log.Any(l => l.Contains("starting it again")));
        time.Advance(XrHost.RestartDelay);

        Assert.Equal(["--home", "--dashboard", "--home"], launcher.Specs.Select(s => s.Arguments));
        Assert.Null(host.Problem);
    }

    [Fact]
    public void A_program_that_exits_three_times_in_a_minute_stays_down_and_says_so()
    {
        homeOn = true;
        dashboardOn = false;
        var host = Host();
        host.MonadoStarted(client, Manifest);

        for (var exit = 1; exit <= XrHost.ExitsAllowed; exit++)
        {
            launcher.Processes[^1].Exit(1);
            WaitFor(() => log.Count(l => l.Contains("stopped")) == exit);
            time.Advance(XrHost.RestartDelay);
        }

        Assert.Equal(XrHost.ExitsAllowed, launcher.Specs.Count);
        Assert.Equal("The home environment keeps stopping. It stays off until Monado starts again.", host.Problem);
    }

    [Fact]
    public async Task Stopping_asks_the_programs_to_quit_kills_what_stays_and_unblocks_the_game()
    {
        var host = InGame();
        host.Line(XrLink.DashboardRole, XrLink.Toggle);
        home.OnQuit = () => launcher.Processes[0].Exit(0);

        var stopping = host.MonadoStoppedAsync();
        time.Advance(XrHost.QuitLimit);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(XrLink.Quit, home.Lines[^1]);
        Assert.Equal(XrLink.Quit, bar.Lines[^1]);
        Assert.False(launcher.Processes[0].Killed);
        Assert.True(launcher.Processes[1].Killed);
        Assert.Equal((Game.Id, false), client.Blocks[^1]);
        Assert.Null(host.Problem);
    }

    [Fact]
    public void A_program_switched_off_while_monado_runs_is_ended_and_not_started_again()
    {
        var host = InGame();
        host.Line(XrLink.DashboardRole, XrLink.Toggle);

        dashboardOn = false;
        host.SwitchesChanged();
        time.Advance(XrHost.QuitLimit);
        WaitFor(() => launcher.Processes[1].Killed);
        time.Advance(XrHost.RestartDelay + XrHost.RestartDelay);

        Assert.Equal(XrLink.Quit, bar.Lines[^1]);
        Assert.Equal((Game.Id, false), client.Blocks[^1]);
        Assert.Equal(2, launcher.Specs.Count);
        Assert.False(launcher.Processes[0].Killed);
    }

    [Fact]
    public void A_program_switched_on_while_monado_runs_starts()
    {
        dashboardOn = false;
        var host = Host();
        host.MonadoStarted(client, Manifest);

        dashboardOn = true;
        host.SwitchesChanged();
        host.SwitchesChanged();

        Assert.Equal(["--home", "--dashboard"], launcher.Specs.Select(s => s.Arguments));
    }

    [Fact]
    public void The_dashboards_process_and_each_opening_and_closing_are_recorded()
    {
        var host = InGame();
        Assert.Equal(["host 102"], record.Events);

        host.Line(XrLink.DashboardRole, XrLink.Toggle);
        host.Line(XrLink.DashboardRole, XrLink.Toggle);
        host.Disconnected(XrLink.DashboardRole, bar);

        Assert.Equal(["host 102", "open", "closed", "host none"], record.Events);
    }

    [Fact]
    public void A_program_nobody_started_is_told_to_quit()
    {
        var host = Host();

        host.Connected(XrLink.HomeRole, home);

        Assert.Equal([XrLink.Quit], home.Lines);
    }

    [Fact]
    public void The_bar_lists_the_hands_first_and_names_a_tracker_by_the_end_of_its_serial()
    {
        var devices = XrHost.BarDevices(
        [
            new MonadoDevice(0, "head", "Headset", "HMD", 0, false, false, 0),
            new MonadoDevice(3, null, "Tracker", "LHR-9F3C4F2A", 0, true, false, 0.4f),
            new MonadoDevice(2, "right", "Index R", "R1", 0, true, true, 1f),
            new MonadoDevice(1, "left", "Index L", "L1", 0, false, false, 0),
        ]);

        Assert.Equal([new XrDevice("L", null, false), new XrDevice("R", 100, true), new XrDevice("4F2A", 40, false)], devices);
    }

    private static void WaitFor(Func<bool> done)
    {
        Assert.True(SpinWait.SpinUntil(done, TimeSpan.FromSeconds(10)));
    }

    private sealed class Program : IXrProgram
    {
        public List<string> Lines { get; } = [];

        public Action? OnQuit { get; set; }

        public void Send(string line)
        {
            Lines.Add(line);
            if (line == XrLink.Quit)
            {
                OnQuit?.Invoke();
            }
        }
    }

    private sealed class Record : IDashboardRecord
    {
        public List<string> Events { get; } = [];

        public void Hosted(int? processId) => Events.Add("host " + (processId?.ToString() ?? "none"));

        public void Opened(bool open) => Events.Add(open ? "open" : "closed");
    }

    private sealed class Exit : IGameExit
    {
        public bool Can { get; set; }

        public int Quits { get; private set; }

        public bool CanQuit() => Can;

        public bool Quit()
        {
            Quits++;
            return true;
        }
    }

    private sealed class Process(int id) : IRunningProcess
    {
        private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id => id;

        public Task<int> Exited => exited.Task;

        public bool Killed { get; private set; }

        public event Action<string>? Line
        {
            add { }
            remove { }
        }

        public event Action<Exception>? ListenerFailed
        {
            add { }
            remove { }
        }

        public void Exit(int code) => exited.TrySetResult(code);

        public void Kill()
        {
            Killed = true;
            exited.TrySetResult(-1);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Launcher : IProcessLauncher
    {
        public List<ProcessSpec> Specs { get; } = [];

        public List<Process> Processes { get; } = [];

        public IRunningProcess Start(ProcessSpec spec)
        {
            lock (Specs)
            {
                Specs.Add(spec);
                var process = new Process(100 + Specs.Count);
                Processes.Add(process);
                return process;
            }
        }
    }

    private sealed class Client : IMonadoClient
    {
        public bool Apps { get; set; } = true;

        public bool StageOffsets { get; set; } = true;

        public bool LocalRecentre { get; set; } = true;

        public int LocalRecentres { get; private set; }

        public IReadOnlyList<MonadoApp> AppList { get; set; } = [];

        public IReadOnlyList<MonadoDevice> DeviceList { get; set; } = [];

        public MonadoPose Stage { get; set; } = MonadoPose.Identity;

        public List<uint> ShownIds { get; } = [];

        public List<(uint Id, bool Blocked)> Blocks { get; } = [];

        public bool OffersOriginOffsets => false;

        public bool OffersApps => Apps;

        public bool OffersInputBlocks => true;

        public bool OffersStageOffset => StageOffsets;

        public IReadOnlyList<MonadoDevice> Devices() => DeviceList;

        public IReadOnlyList<string> TrackingOrigins() => [];

        public MonadoPose GetOriginOffset(uint origin) => MonadoPose.Identity;

        public void SetOriginOffset(uint origin, MonadoPose offset)
        {
        }

        IReadOnlyList<MonadoApp> IMonadoClient.Apps() => AppList;

        public void ShowApp(uint id) => ShownIds.Add(id);

        public void BlockInputs(uint id, bool blocked) => Blocks.Add((id, blocked));

        public MonadoPose GetStageOffset() => Stage;

        public void SetStageOffset(MonadoPose offset) => Stage = offset;

        public bool OffersLocalRecentre => LocalRecentre;

        public void RecentreLocalSpaces() => LocalRecentres++;

        public void Dispose()
        {
        }
    }
}
