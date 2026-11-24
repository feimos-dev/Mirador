using Mirador.Core.OpenVr;

namespace Mirador.Core.Tests.OpenVr;

public sealed class OpenLaminaDashboardTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly TempDir dir = new();
    private readonly Probe probe = new();
    private readonly List<string> log = [];

    public void Dispose() => dir.Dispose();

    private OpenLaminaStore Store() => new(dir.Path);

    private OpenLaminaDashboard Dashboard() => new(Store(), probe, log.Add);

    [Fact]
    public async Task A_dashboard_that_runs_is_named_as_the_host_and_starts_closed()
    {
        probe.Starts[40] = 1337;
        var dashboard = Dashboard();

        dashboard.Hosted(40);
        await dashboard.Flushed().WaitAsync(Limit);

        var data = Store().Read(OpenLaminaStore.Dashboard)!;
        Assert.Equal(40, (int)data["host_pid"]!);
        Assert.Equal(1337ul, (ulong)data["host_start"]!);
        Assert.False((bool)data["open"]!);
    }

    [Fact]
    public async Task Opening_and_closing_follow_in_the_hosts_document()
    {
        probe.Starts[40] = 1337;
        var dashboard = Dashboard();
        dashboard.Hosted(40);

        dashboard.Opened(true);
        await dashboard.Flushed().WaitAsync(Limit);
        Assert.True((bool)Store().Read(OpenLaminaStore.Dashboard)!["open"]!);

        dashboard.Opened(false);
        await dashboard.Flushed().WaitAsync(Limit);
        Assert.False((bool)Store().Read(OpenLaminaStore.Dashboard)!["open"]!);
    }

    [Fact]
    public async Task A_dashboard_that_has_gone_is_taken_out_and_the_rest_kept()
    {
        File.WriteAllText(dir.File("dashboard.json"), """{"format":1,"data":{"active_pid":77}}""");
        probe.Starts[40] = 1337;
        var dashboard = Dashboard();
        dashboard.Hosted(40);
        dashboard.Opened(true);

        dashboard.Hosted(null);
        await dashboard.Flushed().WaitAsync(Limit);

        var data = Store().Read(OpenLaminaStore.Dashboard)!;
        Assert.False(data.ContainsKey("host_pid"));
        Assert.False(data.ContainsKey("host_start"));
        Assert.False((bool)data["open"]!);
        Assert.Equal(77, (int)data["active_pid"]!);
    }

    [Fact]
    public async Task Without_a_host_the_games_own_open_is_not_touched()
    {
        File.WriteAllText(dir.File("dashboard.json"), """{"format":1,"data":{"open":true,"scene_pid":9}}""");
        var dashboard = Dashboard();

        dashboard.Opened(false);
        await dashboard.Flushed().WaitAsync(Limit);

        Assert.True((bool)Store().Read(OpenLaminaStore.Dashboard)!["open"]!);
    }

    [Fact]
    public async Task Without_openlaminas_store_nothing_is_written_or_logged()
    {
        var dashboard = new OpenLaminaDashboard(new OpenLaminaStore(dir.File("none")), probe, log.Add);

        dashboard.Hosted(40);
        dashboard.Opened(true);
        await dashboard.Flushed().WaitAsync(Limit);

        Assert.False(Directory.Exists(dir.File("none")));
        Assert.Empty(log);
        Assert.False(dashboard.CanQuit());
    }

    [Fact]
    public void The_game_in_the_scene_document_can_be_ended_while_that_very_process_runs()
    {
        File.WriteAllText(dir.File("scene.json"), """{"format":1,"data":{"pid":500,"start":133769000000000000,"app_key":"steam.app.1"}}""");
        probe.Starts[500] = 133769000000000000;
        var dashboard = Dashboard();

        Assert.True(dashboard.CanQuit());
        Assert.True(dashboard.Quit());

        Assert.Equal([(500, 133769000000000000ul, OpenLaminaDashboard.QuitGrace)], probe.Ended);
    }

    [Fact]
    public void A_process_that_only_has_the_games_old_id_is_not_ended()
    {
        File.WriteAllText(dir.File("scene.json"), """{"format":1,"data":{"pid":500,"start":133769000000000000}}""");
        probe.Starts[500] = 133769999999999999;
        var dashboard = Dashboard();

        Assert.False(dashboard.CanQuit());
        Assert.False(dashboard.Quit());
        Assert.Empty(probe.Ended);
    }

    [Theory]
    [InlineData("""{"format":1,"data":{}}""")]
    [InlineData("""{"format":1,"data":{"pid":0,"start":5}}""")]
    [InlineData("""{"format":1,"data":{"pid":"500","start":5}}""")]
    [InlineData("""{"format":1,"data":{"pid":500}}""")]
    public void A_scene_document_that_names_no_game_ends_nothing(string scene)
    {
        File.WriteAllText(dir.File("scene.json"), scene);
        probe.Starts[500] = 5;

        Assert.False(Dashboard().CanQuit());
    }

    private sealed class Probe : IProcessProbe
    {
        public Dictionary<int, ulong> Starts { get; } = [];

        public List<(int Id, ulong Start, TimeSpan Grace)> Ended { get; } = [];

        public ulong? StartTime(int processId) => Starts.TryGetValue(processId, out var start) ? start : null;

        public bool End(int processId, ulong startTime, TimeSpan grace)
        {
            Ended.Add((processId, startTime, grace));
            return true;
        }
    }
}
