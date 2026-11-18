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

    /// <summary>A libmonado connection that records the offsets set.</summary>
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
        store.Set(new RoomSetup("steamvr_lh", "1793836993", Lighthouse, Taken));
        store.Set(new RoomSetup("simulated", "1793836993", Simulated, Taken));
        var client = new OriginsClient("steamvr_lh", "simulated");

        Assert.Equal(2, RoomSetupApplier.Apply(client, store, "1793836993"));
        Assert.Equal([(0u, Lighthouse), (1u, Simulated)], client.Set);
    }

    [Fact]
    public void Apply_skips_origins_without_a_setup()
    {
        store.Set(new RoomSetup("simulated", "1793836993", Simulated, Taken));

        // Another universe's setup and one for an origin this Monado hasn't got.
        store.Set(new RoomSetup("steamvr_lh", "30", Lighthouse, Taken));
        store.Set(new RoomSetup("survive", "1793836993", Lighthouse, Taken));
        var client = new OriginsClient("steamvr_lh", "simulated");

        Assert.Equal(1, RoomSetupApplier.Apply(client, store, "1793836993"));
        Assert.Equal([(1u, Simulated)], client.Set);

        Assert.Equal(0, RoomSetupApplier.Apply(new OriginsClient(), store, "1793836993"));
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
    public void A_failed_call_is_passed_on()
    {
        store.Set(new RoomSetup("steamvr_lh", null, Lighthouse, Taken));
        var client = new OriginsClient("steamvr_lh") { Fails = new MiradorException("Monado didn't answer.") };

        Assert.Equal("Monado didn't answer.", Assert.Throws<MiradorException>(() => RoomSetupApplier.Apply(client, store, null)).Message);
    }
}
