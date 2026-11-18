using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Playspace;
using Mirador.Core.Session;
using Mirador.Core.Tests.Build;
using Mirador.Tests;

namespace Mirador.Core.Tests.Playspace;

public sealed class RoomSetupRunTests : IDisposable
{
    private const string Measured =
        """XR-POSES {"head":{"p":[0,1.6,0],"q":[0,0,0,1]},"left":{"p":[0,-0.02,-0.5],"q":[0,0,0,1]},"right":null}""";

    private const string NoHands = """XR-POSES {"head":{"p":[0,1.6,0],"q":[0,0,0,1]},"left":null,"right":null}""";

    private readonly TempDir dir = new();
    private readonly RoomSetupStore store;
    private readonly FakeCommandRunner runner = new();
    private readonly Monado monado = new();

    public RoomSetupRunTests() => store = new RoomSetupStore(dir.File("roomsetup.json"));

    public void Dispose() => dir.Dispose();

    /// <summary>A libmonado connection with a headset and a controller in origin 0; empty once Monado has stopped.</summary>
    private sealed class Client : IMonadoClient
    {
        public bool Stopped { get; set; }

        public Dictionary<uint, MonadoPose> Offsets { get; } = [];

        public bool OffersOriginOffsets => true;

        public IReadOnlyList<MonadoDevice> Devices() =>
            Stopped ? [] : [new(0, "head", "Headset", null, 0, false, false, 0), new(1, "left", "Controller", null, 0, true, false, 1)];

        public IReadOnlyList<string> TrackingOrigins() => Stopped ? [] : ["steamvr_lh"];

        public MonadoPose GetOriginOffset(uint origin) =>
            Stopped ? throw new MiradorException("Monado isn't running.") : Offsets.GetValueOrDefault(origin, MonadoPose.Identity);

        public void SetOriginOffset(uint origin, MonadoPose offset)
        {
            if (Stopped)
            {
                throw new MiradorException("Monado isn't running.");
            }

            Offsets[origin] = offset;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>The session as the step sees it: Monado's connection, how it was started, and starting it again.</summary>
    private sealed class Monado : IRoomSetupSession
    {
        public Client Client { get; } = new();

        public IMonadoClient? Current { get; set; }

        public bool ExitOnDisconnect { get; set; }

        public StartOutcome StartAnswer { get; set; } = new(true, "Monado is running.");

        public int Starts { get; private set; }

        public IMonadoClient? MonadoClient => Current;

        public string? RuntimeManifest => @"C:\Monado\openxr_monado.json";

        public IReadOnlyDictionary<string, string>? MonadoEnvironment =>
            ExitOnDisconnect ? new Dictionary<string, string> { ["IPC_EXIT_ON_DISCONNECT"] = "1" } : null;

        public Exception? UniverseFails { get; set; }

        public string? CurrentUniverse() => UniverseFails is { } fails ? throw fails : "1500000001";

        public Task<StartOutcome> StartMonadoAsync()
        {
            Starts++;
            return Task.FromResult(StartAnswer);
        }

        /// <summary>What "exit on disconnect" does when the measuring program closes.</summary>
        public void StopWithTheClient()
        {
            if (ExitOnDisconnect)
            {
                Client.Stopped = true;
                Current = null;
            }
        }
    }

    private readonly List<string> logged = [];

    private RoomSetupRun Run() => new(new RoomMeasuring(runner, "xr-check.exe"), store, monado, TimeProvider.System)
    {
        StopWait = TimeSpan.FromSeconds(2),
        Log = (what, e) => logged.Add($"{what}: {e.Message}"),
    };

    private void Answer(string line, int code)
    {
        runner.Answer = _ => ([line], code);
        runner.Effect = (_, _) =>
        {
            monado.StopWithTheClient();
            return Task.CompletedTask;
        };
    }

    private Task<RoomStepOutcome> Step(RoomStep step) => Run().RunAsync(step, _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task A_step_sets_the_offset_through_the_running_monado()
    {
        monado.Current = monado.Client;
        Answer(Measured, 0);

        Assert.Equal(new RoomStepOutcome("The floor is set.", RoomStepLevel.Success), await Step(RoomStep.Floor));
        Assert.Equal(0.07f, monado.Client.Offsets[0].Y, 1e-4f);
        Assert.Equal(0.07f, store.Get("steamvr_lh", "1500000001")!.Offset.Y, 1e-4f);
        Assert.Equal(0, monado.Starts);
    }

    [Fact]
    public async Task Monado_stopped_by_the_measurement_is_started_again()
    {
        monado.Current = monado.Client;
        monado.ExitOnDisconnect = true;
        Answer(Measured, 0);

        // The start sets the kept setup.
        Assert.Equal(new RoomStepOutcome("The floor is set.", RoomStepLevel.Success), await Step(RoomStep.Floor));
        Assert.Equal(1, monado.Starts);
        Assert.Equal(0.07f, store.Get("steamvr_lh", "1500000001")!.Offset.Y, 1e-4f);

        monado.Current = monado.Client;
        monado.Client.Stopped = false;
        monado.StartAnswer = new StartOutcome(false, "Close SteamVR first.");
        Assert.Equal(
            new RoomStepOutcome("The floor is measured and kept, but Monado didn't start again: Close SteamVR first.", RoomStepLevel.Warning),
            await Step(RoomStep.Floor));
    }

    [Fact]
    public async Task Monado_stopped_by_a_failed_measurement_is_started_again()
    {
        monado.Current = monado.Client;
        monado.ExitOnDisconnect = true;
        Answer("XR-POSES-FAIL no poses within 30 s (after 12 frames)", 1);

        Assert.Equal(
            new RoomStepOutcome("Mirador couldn't measure: no poses within 30 s (after 12 frames). Monado started again.", RoomStepLevel.Error),
            await Step(RoomStep.Floor));
        Assert.Equal(1, monado.Starts);
        Assert.Null(store.Get("steamvr_lh", "1500000001"));
    }

    [Fact]
    public async Task Monado_stopped_by_a_measurement_without_controllers_is_started_again()
    {
        monado.Current = monado.Client;
        monado.ExitOnDisconnect = true;
        Answer(NoHands, 0);
        monado.StartAnswer = new StartOutcome(false, "The headset isn't plugged in.");

        Assert.Equal(
            new RoomStepOutcome(
                "Neither controller was tracked. Turn one on, lay it on the floor and measure again. Monado didn't start again: The headset isn't plugged in.",
                RoomStepLevel.Error),
            await Step(RoomStep.Floor));
        Assert.Equal(1, monado.Starts);
    }

    [Fact]
    public async Task A_failure_that_didnt_stop_monado_starts_nothing()
    {
        // Without exit on disconnect.
        monado.Current = monado.Client;
        Answer(NoHands, 0);
        Assert.Equal(RoomStepLevel.Error, (await Step(RoomStep.Floor)).Level);
        Assert.Equal(0, monado.Starts);

        // With it, but the measuring program never reached Monado: Monado still runs after the wait.
        monado.ExitOnDisconnect = true;
        runner.Effect = null;
        runner.Answer = _ => (["XR-POSES-FAIL xrCreateInstance failed: XR_ERROR_RUNTIME_UNAVAILABLE"], 1);
        var outcome = await Step(RoomStep.Floor);
        Assert.Equal("Mirador couldn't measure: xrCreateInstance failed: XR_ERROR_RUNTIME_UNAVAILABLE.", outcome.Message);
        Assert.Equal(0, monado.Starts);
    }

    [Fact]
    public async Task Nothing_set_is_not_reported_as_set()
    {
        // Monado closes between the measurement and the offset (no exit on disconnect): the connection gives no origins.
        monado.Current = monado.Client;
        runner.Answer = _ => ([Measured], 0);
        runner.Effect = (_, _) =>
        {
            monado.Client.Stopped = true;
            return Task.CompletedTask;
        };

        Assert.Equal(
            new RoomStepOutcome("The floor is measured and kept; Monado stopped, so it is set when Monado next starts.", RoomStepLevel.Warning),
            await Step(RoomStep.Floor));
        Assert.Equal(0, monado.Starts);
        Assert.NotNull(store.Get("steamvr_lh", "1500000001"));
    }

    [Fact]
    public async Task An_unexpected_failure_is_logged_and_monado_started_again()
    {
        monado.Current = monado.Client;
        monado.ExitOnDisconnect = true;
        monado.UniverseFails = new InvalidOperationException("Something unexpected.");
        Answer(Measured, 0);

        Assert.Equal(
            new RoomStepOutcome("Mirador couldn't set up the room: Something unexpected. Monado started again.", RoomStepLevel.Error),
            await Step(RoomStep.Floor));
        Assert.Equal(1, monado.Starts);
        Assert.Equal(["The room setup step failed: Something unexpected."], logged);
    }

    [Fact]
    public async Task Without_monado_nothing_is_measured()
    {
        Assert.Equal(new RoomStepOutcome("Start Monado first.", RoomStepLevel.Error), await Step(RoomStep.Forward));
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task A_forward_taken_looking_down_is_refused_and_monado_started_again()
    {
        monado.Current = monado.Client;
        monado.ExitOnDisconnect = true;
        Answer("""XR-POSES {"head":{"p":[0,1.6,0],"q":[-0.707107,0,0,0.707107]},"left":null,"right":null}""", 0);

        Assert.Equal(
            new RoomStepOutcome("Look straight ahead, then Set forward. Monado started again.", RoomStepLevel.Error),
            await Step(RoomStep.Forward));
        Assert.Equal(1, monado.Starts);
    }
}
