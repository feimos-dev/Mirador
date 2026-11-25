using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Playspace;
using Mirador.Tests;

namespace Mirador.Core.Tests.Playspace;

public sealed class RoomSetupApplierTests : IDisposable
{
    private static readonly DateTimeOffset Taken = new(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);
    private static readonly MonadoPose Lighthouse = new(0, 0.3826834f, 0, 0.9238795f, 0.1f, -1.6f, 0.2f);
    private static readonly MonadoPose Simulated = MonadoPose.Identity with { Y = -0.5f };

    private readonly TempDir dir = new();
    private readonly RoomSetupStore store;

    public RoomSetupApplierTests() => store = new RoomSetupStore(dir.File("roomsetup.json"));

    public void Dispose() => dir.Dispose();

    private sealed class OriginsClient(params string[] origins) : IMonadoClient
    {
        public bool OffersOriginOffsets { get; init; } = true;

        public List<(uint Origin, MonadoPose Offset)> Set { get; } = [];

        public MiradorException? Fails { get; init; }

        public IReadOnlyList<MonadoDevice> Devices() => [];

        public IReadOnlyList<string> TrackingOrigins() => OffersOriginOffsets ? origins : throw new NotSupportedException();

        public MonadoPose GetOriginOffset(uint origin) => OffersOriginOffsets ? MonadoPose.Identity : throw new NotSupportedException();

        public void SetOriginOffset(uint origin, MonadoPose offset)
        {
            if (!OffersOriginOffsets)
            {
                throw new NotSupportedException();
            }

            if (Fails is not null)
            {
                throw Fails;
            }

            Set.Add((origin, offset));
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Apply_sets_each_stored_origin_by_name()
    {
        store.Set(new RoomSetup("steamvr_lh", "1500000001", Lighthouse, Taken));
        store.Set(new RoomSetup("simulated", "1500000001", Simulated, Taken));
        var client = new OriginsClient("steamvr_lh", "simulated");

        Assert.Equal(2, RoomSetupApplier.Apply(client, store, "1500000001"));
        Assert.Equal([(0u, Lighthouse), (1u, Simulated)], client.Set);
    }

    [Fact]
    public void Apply_skips_origins_without_a_setup()
    {
        store.Set(new RoomSetup("simulated", "1500000001", Simulated, Taken));

        store.Set(new RoomSetup("steamvr_lh", "30", Lighthouse, Taken));
        store.Set(new RoomSetup("survive", "1500000001", Lighthouse, Taken));
        var client = new OriginsClient("steamvr_lh", "simulated");

        Assert.Equal(1, RoomSetupApplier.Apply(client, store, "1500000001"));
        Assert.Equal([(1u, Simulated)], client.Set);

        Assert.Equal(0, RoomSetupApplier.Apply(new OriginsClient(), store, "1500000001"));
    }

    [Fact]
    public void A_setup_taken_without_a_lighthouse_space_is_the_fallback()
    {
        var noSpace = MonadoPose.Identity with { Y = -0.75f };
        store.Set(new RoomSetup("steamvr_lh", null, noSpace, Taken));
        store.Set(new RoomSetup("simulated", null, noSpace, Taken));
        store.Set(new RoomSetup("simulated", "1500000001", Simulated, Taken));
        var client = new OriginsClient("steamvr_lh", "simulated");

        Assert.Equal(2, RoomSetupApplier.Apply(client, store, "1500000001"));
        Assert.Equal([(0u, noSpace), (1u, Simulated)], client.Set);
    }

    [Fact]
    public void Without_a_lighthouse_space_only_setups_taken_without_one_apply()
    {
        store.Set(new RoomSetup("steamvr_lh", "1500000001", Lighthouse, Taken));
        store.Set(new RoomSetup("simulated", null, Simulated, Taken));
        var client = new OriginsClient("steamvr_lh", "simulated");

        Assert.Equal(1, RoomSetupApplier.Apply(client, store, null));
        Assert.Equal([(1u, Simulated)], client.Set);
    }

    [Fact]
    public void Apply_does_nothing_without_origin_offsets()
    {
        store.Set(new RoomSetup("steamvr_lh", null, Lighthouse, Taken));
        var client = new OriginsClient("steamvr_lh") { OffersOriginOffsets = false };

        Assert.Equal(0, RoomSetupApplier.Apply(client, store, null));
        Assert.Empty(client.Set);
    }

    [Fact]
    public void At_a_start_the_universe_comes_from_steams_files()
    {
        using var steam = new TempDir();
        Directory.CreateDirectory(Path.GetDirectoryName(Mirador.Core.Lighthouse.SteamVrRoom.LighthousedbPath(steam.Path))!);
        File.WriteAllText(
            Mirador.Core.Lighthouse.SteamVrRoom.LighthousedbPath(steam.Path),
            """{ "known_universes" : [ { "base_stations" : [ { "base_serial_number" : 3735928559 } ], "id" : "1500000001" } ] }""");
        store.Set(new RoomSetup("steamvr_lh", "1500000001", Lighthouse, Taken));
        store.Set(new RoomSetup("steamvr_lh", null, Simulated, Taken));
        var client = new OriginsClient("steamvr_lh");

        Assert.Equal(1, RoomSetupApplier.ApplyWithSteamVr(client, store, steam.Path, [new("AA:BB:CC:DD:EE:01", "LHB-DEADBEEF")]));
        Assert.Equal([(0u, Lighthouse)], client.Set);

        client.Set.Clear();
        Assert.Equal(1, RoomSetupApplier.ApplyWithSteamVr(client, store, null, []));
        Assert.Equal([(0u, Simulated)], client.Set);
    }

    [Fact]
    public void Steams_files_that_cant_be_read_apply_nothing()
    {
        using var steam = new TempDir();
        Directory.CreateDirectory(Path.GetDirectoryName(Mirador.Core.Lighthouse.SteamVrRoom.LighthousedbPath(steam.Path))!);
        File.WriteAllText(Mirador.Core.Lighthouse.SteamVrRoom.LighthousedbPath(steam.Path), "{ \"known_universes\" : [");
        store.Set(new RoomSetup("steamvr_lh", null, Simulated, Taken));
        var client = new OriginsClient("steamvr_lh");

        var e = Assert.Throws<MiradorException>(() => RoomSetupApplier.ApplyWithSteamVr(client, store, steam.Path, []));
        Assert.Equal("Couldn't read SteamVR's Lighthouse files; try again.", e.Message);
        Assert.Empty(client.Set);
    }

    [Fact]
    public void A_failed_call_is_passed_on()
    {
        store.Set(new RoomSetup("steamvr_lh", null, Lighthouse, Taken));
        var client = new OriginsClient("steamvr_lh") { Fails = new MiradorException("Monado didn't answer.") };

        Assert.Equal("Monado didn't answer.", Assert.Throws<MiradorException>(() => RoomSetupApplier.Apply(client, store, null)).Message);
    }
}
