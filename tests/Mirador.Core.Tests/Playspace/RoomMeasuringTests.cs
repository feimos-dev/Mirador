using Mirador.Core.Build;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Playspace;
using Mirador.Core.Tests.Build;

namespace Mirador.Core.Tests.Playspace;

public class RoomMeasuringTests
{
    private const float Tolerance = 1e-4f;
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);

    private const string Measured =
        """XR-POSES {"head":{"p":[0,1.6,0],"q":[0,-0.707107,0,0.707107]},"left":{"p":[0,-0.02,-0.5],"q":[0,0,0,1]},"right":null}""";

    /// <summary>Monado's devices and tracking origins as libmonado gives them.</summary>
    private sealed class DevicesClient(IReadOnlyList<MonadoDevice> devices, params string[] origins) : IMonadoClient
    {
        public bool OffersOriginOffsets { get; init; } = true;

        public Dictionary<uint, MonadoPose> Offsets { get; } = [];

        public IReadOnlyList<MonadoDevice> Devices() => devices;

        public IReadOnlyList<string> TrackingOrigins() => OffersOriginOffsets ? origins : throw new NotSupportedException();

        public MonadoPose GetOriginOffset(uint origin) =>
            OffersOriginOffsets ? Offsets.GetValueOrDefault(origin, MonadoPose.Identity) : throw new NotSupportedException();

        public void SetOriginOffset(uint origin, MonadoPose offset) => Offsets[origin] = offset;

        public void Dispose()
        {
        }
    }

    private static MonadoDevice Device(string? role, uint origin) => new(0, role, role ?? "tracker", null, origin, false, false, 0);

    [Fact]
    public void The_origins_of_the_headset_and_controllers_are_measured()
    {
        var client = new DevicesClient(
            [Device("head", 1), Device("left", 0), Device("right", 0), Device(null, 2)],
            "steamvr_lh", "Pimax", "trackers");
        client.Offsets[1] = MonadoPose.Identity with { Y = 0.2f };

        var origins = RoomMeasuring.TrackedOrigins(client);

        Assert.Equal([new TrackedOrigin(0, "steamvr_lh", MonadoPose.Identity), new TrackedOrigin(1, "Pimax", MonadoPose.Identity with { Y = 0.2f })], origins);
    }

    [Fact]
    public void No_headset_or_controllers_or_no_offsets_are_refused()
    {
        Assert.Throws<MiradorException>(() => RoomMeasuring.TrackedOrigins(new DevicesClient([Device(null, 0)], "trackers")));
        Assert.Throws<MiradorException>(() => RoomMeasuring.TrackedOrigins(new DevicesClient([Device("head", 5)], "steamvr_lh")));

        var e = Assert.Throws<MiradorException>(() => RoomMeasuring.TrackedOrigins(new DevicesClient([Device("head", 0)], "steamvr_lh") { OffersOriginOffsets = false }));
        Assert.Equal(Libmonado.NotOffered, e.Message);
    }

    [Fact]
    public async Task Measuring_runs_xr_check_against_the_running_monado()
    {
        var runner = new FakeCommandRunner { Answer = _ => (["log", Measured], 0) };

        var poses = await new RoomMeasuring(runner, @"C:\Mirador\tools\xr-check.exe").MeasureAsync(@"C:\Monado\openxr_monado.json", CancellationToken.None);

        Assert.Equal(-0.02f, poses.Left!.Value.Y, Tolerance);
        var spec = Assert.Single(runner.Commands);
        Assert.Equal(@"C:\Mirador\tools\xr-check.exe", spec.FileName);
        Assert.Equal(["--api", "d3d11", "--poses", "--timeout", "30"], spec.Arguments);
        Assert.Equal(@"C:\Monado\openxr_monado.json", spec.Environment!["XR_RUNTIME_JSON"]);
    }

    [Fact]
    public async Task A_failed_measurement_says_why()
    {
        var runner = new FakeCommandRunner { Answer = _ => (["XR-POSES-FAIL no poses within 30 s (after 0 frames)"], 1) };
        var e = await Assert.ThrowsAsync<MiradorException>(() => new RoomMeasuring(runner, "xr-check.exe").MeasureAsync(null, CancellationToken.None));
        Assert.Equal("Mirador couldn't measure: no poses within 30 s (after 0 frames).", e.Message);
        Assert.Null(runner.Commands[0].Environment);

        runner.Answer = _ => (["something else"], 3);
        e = await Assert.ThrowsAsync<MiradorException>(() => new RoomMeasuring(runner, "xr-check.exe").MeasureAsync(null, CancellationToken.None));
        Assert.Equal("The measuring program gave no result (exit code 3).", e.Message);

        // A result line with a failing exit code isn't trusted.
        runner.Answer = _ => ([Measured], 1);
        e = await Assert.ThrowsAsync<MiradorException>(() => new RoomMeasuring(runner, "xr-check.exe").MeasureAsync(null, CancellationToken.None));
        Assert.Equal("The measuring program gave no result (exit code 1).", e.Message);
    }

    [Fact]
    public async Task A_measurement_that_hangs_is_ended()
    {
        var runner = new FakeCommandRunner { Effect = (_, ct) => Task.Delay(Timeout.Infinite, ct) };
        var measuring = new RoomMeasuring(runner, "xr-check.exe") { Limit = TimeSpan.FromMilliseconds(50) };

        var e = await Assert.ThrowsAsync<MiradorException>(() => measuring.MeasureAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("The measuring program didn't finish within 0 s.", e.Message);
    }

    [Fact]
    public void The_floor_step_lifts_every_measured_origin()
    {
        var poses = XrPoses.Parse([Measured])!;
        TrackedOrigin[] origins = [new(0, "steamvr_lh", MonadoPose.Identity), new(1, "Pimax", MonadoPose.Identity with { Y = 0.2f })];

        var setups = RoomMeasuring.Corrected(origins, RoomStep.Floor, poses, "1793836993", Now);

        // The left grip rests at −0.02 m: the floor is at −0.07 m.
        Assert.Equal(["steamvr_lh", "Pimax"], setups.Select(s => s.OriginName));
        Assert.All(setups, s => Assert.Equal("1793836993", s.Universe));
        Assert.All(setups, s => Assert.Equal(Now, s.Taken));
        Assert.Equal(0.07f, setups[0].Offset.Y, Tolerance);
        Assert.Equal(0.27f, setups[1].Offset.Y, Tolerance);
        Assert.Equal(1, setups[0].Offset.Qw, Tolerance);
    }

    [Fact]
    public void The_forward_step_turns_every_measured_origin()
    {
        // The head faces +X (−90°): the origins turn by +90°.
        var poses = XrPoses.Parse([Measured])!;
        var setups = RoomMeasuring.Corrected([new(0, "steamvr_lh", MonadoPose.Identity)], RoomStep.Forward, poses, null, Now);

        var setup = Assert.Single(setups);
        Assert.Null(setup.Universe);
        Assert.Equal(MathF.Sqrt(0.5f), setup.Offset.Qy, Tolerance);
        Assert.Equal(MathF.Sqrt(0.5f), setup.Offset.Qw, Tolerance);
        Assert.Equal(0, setup.Offset.Y, Tolerance);
    }

    [Fact]
    public void The_floor_step_needs_a_controller()
    {
        var poses = XrPoses.Parse(["""XR-POSES {"head":{"p":[0,1.6,0],"q":[0,0,0,1]},"left":null,"right":null}"""])!;

        var e = Assert.Throws<MiradorException>(() => RoomMeasuring.Corrected([new(0, "steamvr_lh", MonadoPose.Identity)], RoomStep.Floor, poses, null, Now));
        Assert.Equal("Neither controller was tracked. Turn one on, lay it on the floor and measure again.", e.Message);
    }

    [Fact]
    public void Monado_stops_with_the_measuring_program_when_it_exits_on_disconnect()
    {
        Assert.True(RoomMeasuring.StopsMonado(new Dictionary<string, string> { ["IPC_EXIT_ON_DISCONNECT"] = "1" }));
        Assert.False(RoomMeasuring.StopsMonado(new Dictionary<string, string> { ["XRT_COMPOSITOR_SCALE_PERCENTAGE"] = "100" }));
        Assert.False(RoomMeasuring.StopsMonado(null));
    }
}
