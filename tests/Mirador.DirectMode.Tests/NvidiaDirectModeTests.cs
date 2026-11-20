using Microsoft.Extensions.Time.Testing;
using Mirador.Core;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.DirectMode.Tests;

public sealed class NvidiaDirectModeTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private const string HeadsetMonitorId = @"\\?\DISPLAY#PVR1019#5&abc&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private readonly TempDir dir = new();
    private readonly List<string> steps = [];
    private readonly TimerWatch time = new(new FakeTimeProvider());
    private readonly FakeConsoleStarter consoles;
    private readonly AppPaths paths;
    private readonly DirectModeStateStore state;
    private readonly FakeHelper helper = new();
    private readonly string bundledLayer;
    private string? nvidia = "32.0.16.2101";
    private IReadOnlyList<MonitorEntry> monitors = [];
    private bool monadoRuns;

    /// <summary>What the display check writes and its exit code.</summary>
    private (string[] Lines, int ExitCode) check = (["PASS: 1800 flips, 0 missed"], 0);

    public NvidiaDirectModeTests()
    {
        paths = new AppPaths(Path.Combine(dir.Path, "Mirador"));
        state = new DirectModeStateStore(paths.DirectModeFile);
        state.Save(new DirectModeState("621.01"));
        bundledLayer = Path.Combine(dir.Path, "app", "layer");
        dir.Touch("app", "layer", DirectModeLayer.ManifestName);
        dir.Touch("app", "layer", DirectModeLayer.DllName);
        dir.Touch("app", "layer", DirectModeLayer.CheckExeName);
        consoles = new FakeConsoleStarter(spec =>
        {
            steps.Add(spec.Arguments.Length == 0 ? "display check" : "display check " + spec.Arguments);
            var display = new FakeConsole(check.Lines);
            display.End(check.ExitCode);
            return display;
        });
    }

    public void Dispose() => dir.Dispose();

    private ModuleContext Context => new(paths, helper, new FakeProcesses(), new FakeUsbDevices(), time);

    private NvidiaDirectMode Layer() => new(new NvidiaDirectModeDeps(
        bundledLayer, consoles.Start, state, () => nvidia, () => monitors, () => monadoRuns));

    private Task<ModuleSession> BeforeStart() =>
        Layer().BeforeStartAsync(Context, new Progress<string>(), CancellationToken.None).WaitAsync(Limit);

    [Fact]
    public async Task Checks_find_the_gpu_and_the_layer()
    {
        var results = await Layer().CheckAsync(Context, CancellationToken.None);

        Assert.Equal(
            [
                new CheckResult("NVIDIA GPU", StatusLevel.Ok, "Driver 621.01"),
                new CheckResult("Vulkan layer", StatusLevel.Ok, "Comes with Mirador"),
            ],
            results);
    }

    [Fact]
    public async Task No_nvidia_gpu_is_an_error_that_points_at_the_profile()
    {
        nvidia = null;

        var results = await Layer().CheckAsync(Context, CancellationToken.None);

        Assert.Equal(
            new CheckResult("NVIDIA GPU", StatusLevel.Error, "No NVIDIA GPU found.", "Set the profile's direct-mode layer to None."),
            results[0]);
    }

    [Fact]
    public async Task A_driver_version_mirador_cant_read_is_shown_as_windows_gives_it()
    {
        nvidia = "1.2.3";

        var results = await Layer().CheckAsync(Context, CancellationToken.None);

        Assert.Equal("Driver 1.2.3", results[0].Text);
    }

    [Fact]
    public async Task A_missing_layer_is_an_error()
    {
        File.Delete(Path.Combine(bundledLayer, DirectModeLayer.DllName));

        var results = await Layer().CheckAsync(Context, CancellationToken.None);

        Assert.Equal(StatusLevel.Error, results[1].Level);
        Assert.StartsWith("The direct-mode layer that comes with Mirador is incomplete", results[1].Text);
    }

    [Fact]
    public async Task A_headset_on_the_desktop_gives_the_direct_mode_warning()
    {
        monitors = [new MonitorEntry(HeadsetMonitorId, true)];

        var results = await Layer().CheckAsync(Context, CancellationToken.None);

        Assert.Equal(
            new CheckResult(
                IDirectMode.HeadsetOnDesktopLabel, StatusLevel.Warning,
                "The headset shows as a normal monitor, so Monado can't use it. Fix direct mode turns it back into a headset display."),
            results[^1]);
    }

    [Fact]
    public async Task No_warning_while_the_headset_is_off_the_desktop_or_monado_runs_or_without_an_nvidia_gpu()
    {
        monitors = [new MonitorEntry(HeadsetMonitorId, false)];
        Assert.DoesNotContain(await Layer().CheckAsync(Context, CancellationToken.None), r => r.Label == IDirectMode.HeadsetOnDesktopLabel);

        monitors = [new MonitorEntry(HeadsetMonitorId, true)];
        monadoRuns = true;
        Assert.DoesNotContain(await Layer().CheckAsync(Context, CancellationToken.None), r => r.Label == IDirectMode.HeadsetOnDesktopLabel);

        monadoRuns = false;
        nvidia = null;
        Assert.DoesNotContain(await Layer().CheckAsync(Context, CancellationToken.None), r => r.Label == IDirectMode.HeadsetOnDesktopLabel);
    }

    [Fact]
    public async Task The_session_gives_monado_the_layers_variables_and_takes_out_its_test_switches()
    {
        using var session = (DirectModeSession)await BeforeStart();

        var env = MonadoEnvironment.Build(
            new Dictionary<string, string> { ["MIRADOR_DIRECT_MODE_IGNORE_HOLDERS"] = "1" },
            new MonadoSettings(),
            new Profile("p1", "P", null, BuildSource.Mirador, [], new Dictionary<string, string>()),
            session.Environment);

        foreach (var (name, value) in DirectModeEnvironment.Variables)
        {
            Assert.Equal(value, env[name]);
        }

        Assert.False(env.ContainsKey("MIRADOR_DIRECT_MODE_IGNORE_HOLDERS"));
        Assert.False(env.ContainsKey("VK_ADD_IMPLICIT_LAYER_PATH"));
        Assert.Empty(session.RecoveryData);
    }

    [Fact]
    public async Task A_start_has_the_helper_register_the_layer_by_its_kind()
    {
        using var session = (DirectModeSession)await BeforeStart();

        Assert.Equal([new HelperRequest(HelperCommand.RegisterLayer, Layer: "nvidia")], helper.Sent);
    }

    [Fact]
    public async Task A_layer_the_helper_cant_register_refuses_the_start()
    {
        helper.Reply = new HelperReply(false, "Mirador's helper service isn't running.");

        var e = await Assert.ThrowsAsync<MiradorException>(BeforeStart);

        Assert.Equal("Mirador's helper service isn't running.", e.Message);
        Assert.Empty(steps);
    }

    [Fact]
    public async Task A_layer_no_profile_uses_is_taken_out()
    {
        Assert.Null(await Layer().SetRegisteredAsync(Context, used: false, CancellationToken.None));

        Assert.Equal([new HelperRequest(HelperCommand.UnregisterLayer, Layer: "nvidia")], helper.Sent);
    }

    [Fact]
    public async Task A_missing_layer_refuses_the_start()
    {
        File.Delete(Path.Combine(bundledLayer, DirectModeLayer.DllName));

        var e = await Assert.ThrowsAsync<MiradorException>(BeforeStart);

        Assert.StartsWith("The direct-mode layer that comes with Mirador is incomplete", e.Message);
    }

    [Fact]
    public async Task The_same_driver_isnt_checked_again()
    {
        using var session = (DirectModeSession)await BeforeStart();

        Assert.Empty(steps);
    }

    [Fact]
    public async Task A_new_driver_runs_the_display_check_and_a_pass_remembers_it()
    {
        state.Save(new DirectModeState());

        using var session = (DirectModeSession)await BeforeStart();

        Assert.Equal(["display check"], steps);
        Assert.Equal("621.01", state.Load().LastGoodDriver);
    }

    [Fact]
    public async Task A_new_driver_that_fails_the_display_check_refuses_the_start()
    {
        state.Save(new DirectModeState("620.00"));
        check = (["FAIL: NVAPI call 0x1F2E3D4C is missing in this driver", "what to do: go back to driver 620.00"], 1);

        var e = await Assert.ThrowsAsync<MiradorException>(BeforeStart);

        Assert.Equal(
            "Direct mode didn't pass its check with NVIDIA driver 621.01: NVAPI call 0x1F2E3D4C is missing in this driver. Go back to driver 620.00.",
            e.Message);
        Assert.Equal("620.00", state.Load().LastGoodDriver);
        Assert.Contains("NVAPI call 0x1F2E3D4C", File.ReadAllText(NvidiaDirectMode.DisplayCheckFile(paths)));
    }

    [Fact]
    public async Task A_display_check_that_never_finished_isnt_called_a_verdict()
    {
        state.Save(new DirectModeState());
        consoles.Refusal = new MiradorException("Couldn't start mirador-display-check.exe: Access is denied.");

        var e = await Assert.ThrowsAsync<MiradorException>(BeforeStart);

        Assert.Equal(
            "Mirador couldn't check direct mode with the new NVIDIA driver 621.01: Couldn't start mirador-display-check.exe: Access is denied. Try again.",
            e.Message);
    }

    [Fact]
    public async Task A_headset_the_layer_leaves_to_windows_is_a_warning_at_the_first_look_and_no_display_wait()
    {
        state.Save(new DirectModeState());
        check = (["NOT USED: the headset's EDID carries Microsoft's HMD block"], 0);
        using var session = (DirectModeSession)await BeforeStart();
        var warnings = new List<SessionWarning>();
        var reasons = new List<string>();
        session.Warning += w => { lock (warnings) { warnings.Add(w); } };
        session.StopRequested += reasons.Add;

        Poll(20);

        lock (warnings)
        {
            var warning = Assert.Single(warnings);
            Assert.Equal("direct-mode", warning.Key);
        }

        Assert.Empty(reasons);
    }

    [Fact]
    public async Task A_display_that_never_opens_asks_to_stop_monado_once()
    {
        using var session = (DirectModeSession)await BeforeStart();
        var reasons = new List<string>();
        session.StopRequested += reasons.Add;
        session.OnServiceLine("[dvl] warn: NVAPI EnableDirectMode for vendor PVR returned -5; still waiting for the display");

        Poll(11);
        Assert.Empty(reasons);

        Poll(5);

        var reason = Assert.Single(reasons);
        Assert.StartsWith("The headset's display didn't come up within a minute. ", reason);
        Assert.Contains("still waiting for the display", reason);
    }

    [Fact]
    public async Task Ending_stops_the_display_wait()
    {
        var session = (DirectModeSession)await BeforeStart();
        var reasons = new List<string>();
        session.StopRequested += reasons.Add;
        await session.EndAsync(CancellationToken.None);

        Poll(20);

        Assert.Empty(reasons);
    }

    [Fact]
    public async Task Check_display_remembers_a_good_driver()
    {
        state.Save(new DirectModeState());

        var result = await Layer().CheckDisplayAsync(Context, CancellationToken.None);

        Assert.Equal(new CheckResult("Display check", StatusLevel.Ok, "Direct mode works: 1800 flips, 0 missed."), result);
        Assert.Equal(["display check"], steps);
        Assert.Equal("621.01", state.Load().LastGoodDriver);
    }

    [Fact]
    public async Task A_failed_display_check_keeps_the_last_good_driver_and_gives_its_advice()
    {
        check = (["FAIL: no flips", "what to do: plug the headset in"], 1);

        var result = await Layer().CheckDisplayAsync(Context, CancellationToken.None);

        Assert.Equal(new CheckResult("Display check", StatusLevel.Error, "Direct mode doesn't work: no flips. What to do: plug the headset in."), result);
        Assert.Equal("621.01", state.Load().LastGoodDriver);
    }

    [Fact]
    public async Task Fix_runs_the_check_with_enable_direct_mode()
    {
        check = (["PASS: direct mode is on for the headset."], 0);

        var problem = await Layer().FixAsync(Context, CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal(["display check --enable-direct-mode"], steps);
    }

    [Fact]
    public async Task A_failed_fix_gives_the_checks_message_and_advice_as_they_are()
    {
        check = (["FAIL: NVAPI EnableDirectMode for the headset (vendor 0xd242) returned -5", "what to do: run this as administrator."], 1);

        var problem = await Layer().FixAsync(Context, CancellationToken.None);

        Assert.Equal("NVAPI EnableDirectMode for the headset (vendor 0xd242) returned -5. Run this as administrator.", problem);
    }

    [Fact]
    public async Task A_fix_that_leaves_the_headset_on_the_desktop_says_so()
    {
        check = (["PASS: direct mode is on for the headset."], 0);
        monitors = [new MonitorEntry(HeadsetMonitorId, true)];

        var problem = await Layer().FixAsync(Context, CancellationToken.None);

        Assert.Equal(
            "Direct mode was turned on, but the headset still shows as a monitor. Unplug and plug in the headset's DisplayPort cable, then try again.",
            problem);
    }

    private void Poll(int times)
    {
        for (var i = 0; i < times; i++)
        {
            time.Clock.Advance(DirectModeSession.PollInterval);
        }
    }

    private sealed class FakeHelper : IHelperClient
    {
        public List<HelperRequest> Sent { get; } = [];

        public HelperReply Reply { get; set; } = new(true);

        public Task<HelperReply> SendAsync(HelperCommand command, CancellationToken ct) => SendAsync(new HelperRequest(command), ct);

        public Task<HelperReply> SendAsync(HelperRequest request, CancellationToken ct)
        {
            Sent.Add(request);
            return Task.FromResult(Reply);
        }
    }
}
