using Mirador.Core.Lighthouse;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Playspace;
using Mirador.Tests;

namespace Mirador.Core.Tests.Playspace;

public sealed class RoomSetupStoreTests : IDisposable
{
    private static readonly DateTimeOffset Taken = new(2026, 11, 18, 9, 30, 0, TimeSpan.FromHours(1));
    private static readonly MonadoPose Lowered = new(0, 0.7071068f, 0, 0.7071068f, 0.25f, -1.5f, 0.125f);

    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private string File => dir.File("roomsetup.json");

    private RoomSetupStore Store() => new(File);

    [Fact]
    public void Set_get_and_clear_round_trip()
    {
        Assert.Null(Store().Get("steamvr_lh", "1500000001"));

        var setup = new RoomSetup("steamvr_lh", "1500000001", Lowered, Taken);
        Store().Set(setup);
        Assert.Equal(setup, Store().Get("steamvr_lh", "1500000001"));
        Assert.False(System.IO.File.Exists(File + ".tmp"));

        var again = setup with { Offset = MonadoPose.Identity, Taken = Taken.AddMinutes(5) };
        Store().Set(again);
        Assert.Equal(again, Store().Get("steamvr_lh", "1500000001"));
        Assert.Single(Store().All());

        Store().Clear("steamvr_lh", "1500000001");
        Assert.Null(Store().Get("steamvr_lh", "1500000001"));
        Assert.Empty(Store().All());

        Store().Clear("steamvr_lh", "1500000001");
    }

    [Fact]
    public void A_universe_keeps_its_own_setup()
    {
        var current = new RoomSetup("steamvr_lh", "1500000001", Lowered, Taken);
        var other = new RoomSetup("steamvr_lh", "30", MonadoPose.Identity with { Y = -1.2f }, Taken);
        var none = new RoomSetup("steamvr_lh", null, MonadoPose.Identity with { Y = -0.9f }, Taken);
        var simulated = new RoomSetup("simulated", "1500000001", MonadoPose.Identity with { X = 1 }, Taken);
        Store().Set(current);
        Store().Set(other);
        Store().Set(none);
        Store().Set(simulated);

        Assert.Equal(current, Store().Get("steamvr_lh", "1500000001"));
        Assert.Equal(other, Store().Get("steamvr_lh", "30"));
        Assert.Equal(none, Store().Get("steamvr_lh", null));
        Assert.Equal(simulated, Store().Get("simulated", "1500000001"));
        Assert.Null(Store().Get("simulated", null));
        Assert.Null(Store().Get("steamvr_lh", "2"));

        Store().Clear("steamvr_lh", "30");
        Assert.Null(Store().Get("steamvr_lh", "30"));
        Assert.Equal(current, Store().Get("steamvr_lh", "1500000001"));
        Assert.Equal(none, Store().Get("steamvr_lh", null));
    }

    [Fact]
    public void The_steamvr_room_setup_it_was_taken_on_is_kept()
    {
        var setup = new RoomSetup("steamvr_lh", "1500000001", Lowered, Taken, new SteamVrBase("30", 0.25f, -1.5f, 0.125f, 1));
        Store().Set(setup);
        Store().Set(new RoomSetup("simulated", null, Lowered, Taken, SteamVrBase.None));

        Assert.Equal(setup, Store().Get("steamvr_lh", "1500000001"));
        Assert.Equal(SteamVrBase.None, Store().Get("simulated", null)!.Base);
        Assert.Contains("\"base\"", System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void A_file_that_cant_be_read_is_set_aside()
    {
        System.IO.File.WriteAllText(File, "not json");

        Assert.Null(Store().Get("steamvr_lh", null));
        Assert.True(System.IO.File.Exists(File + ".bad"));

        Store().Set(new RoomSetup("steamvr_lh", null, Lowered, Taken));
        Assert.Equal(Lowered, Store().Get("steamvr_lh", null)?.Offset);
    }

    [Fact]
    public void Entries_without_an_origin_or_a_rotation_are_left_out()
    {
        System.IO.File.WriteAllText(File, """
            { "setups" : [
                null,
                { "universe" : "30", "offset" : { "qw" : 1 }, "taken" : "2026-11-18T09:30:00+01:00" },
                { "originName" : "", "offset" : { "qw" : 1 }, "taken" : "2026-11-18T09:30:00+01:00" },
                { "originName" : "simulated", "offset" : { "y" : -1.5 }, "taken" : "2026-11-18T09:30:00+01:00" },
                { "originName" : "simulated", "universe" : "30", "offset" : { "qw" : 2 }, "taken" : "2026-11-18T09:30:00+01:00" },
                { "originName" : "steamvr_lh", "universe" : "30", "offset" : { "qx" : 0, "qy" : 0, "qz" : 0, "qw" : 1, "x" : 0, "y" : -1.5, "z" : 0 }, "taken" : "2026-11-18T09:30:00+01:00" }
            ] }
            """);

        var kept = Assert.Single(Store().All());
        Assert.Equal(new RoomSetup("steamvr_lh", "30", MonadoPose.Identity with { Y = -1.5f }, Taken), kept);
        Assert.True(System.IO.File.Exists(File + ".bad"));
    }
}
