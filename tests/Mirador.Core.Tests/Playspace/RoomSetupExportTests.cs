using Mirador.Core.Lighthouse;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Playspace;
using Mirador.Tests;

namespace Mirador.Core.Tests.Playspace;

public sealed class RoomSetupExportTests : IDisposable
{
    private const string Universe = "1500000001";
    private const string Origin = SteamVrRoom.LighthouseOrigin;
    private static readonly DateTimeOffset Taken = new(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);
    private static readonly MonadoPose Lifted = MonadoPose.Identity with { Y = 0.1f };

    /// <summary>
    /// One space with SteamVR's room setup (30, not at the origin and turned): steamvr_lh uses it now. The space in use
    /// is listed first, without one.
    /// </summary>
    private const string Vrchap = """
        {
           "jsonid" : "chaperone_info",
           "universes" : [
              {
                 "collision_bounds" : [],
                 "play_area" : [ 2, 2 ],
                 "seated" : { "translation" : [ 0, 0, 0 ], "yaw" : 0 },
                 "standing" : { "translation" : [ 0.25, -1.5, 0.125 ], "yaw" : 1 },
                 "time" : "Tue Nov 18 09:30:00 2026",
                 "universeID" : "30"
              }
           ],
           "version" : 5
        }
        """;

    private readonly TempDir steam = new();
    private readonly RoomSetupStore store;

    public RoomSetupExportTests()
    {
        store = new RoomSetupStore(steam.File("roomsetup.json"));
        var vrchap = SteamVrRoom.ChaperoneInfoPath(steam.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(vrchap)!);
        File.WriteAllText(vrchap, Vrchap);
        var db = SteamVrRoom.LighthousedbPath(steam.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        File.WriteAllText(db, """{ "known_universes" : [ { "id" : "1500000001" }, { "id" : "30" } ] }""");
    }

    public void Dispose() => steam.Dispose();

    /// <summary>A libmonado connection with the steamvr_lh origin, recording the offset set.</summary>
    private sealed class LighthouseClient : IMonadoClient
    {
        public MonadoPose? Set { get; private set; }

        public bool OffersOriginOffsets => true;

        public IReadOnlyList<MonadoDevice> Devices() => [];

        public IReadOnlyList<string> TrackingOrigins() => [Origin];

        public MonadoPose GetOriginOffset(uint origin) => MonadoPose.Identity;

        public void SetOriginOffset(uint origin, MonadoPose offset) => Set = offset;

        public void Dispose()
        {
        }
    }

    private string? Refusal(bool steamVr = false, bool monado = false) =>
        RoomSetupExport.Refusal(steamVr, monado, steam.Path, Universe, store);

    [Fact]
    public void Steamvr_first_then_monado_must_be_closed()
    {
        store.Set(new RoomSetup(Origin, Universe, Lifted, Taken));

        Assert.Equal("Close SteamVR first.", Refusal(steamVr: true, monado: true));
        Assert.Equal("Stop Monado first.", Refusal(monado: true));
        Assert.Null(Refusal());
    }

    [Fact]
    public void Without_steam_a_space_or_a_setup_there_is_nothing_to_export()
    {
        Assert.Equal("Steam isn't installed.", RoomSetupExport.Refusal(false, false, null, Universe, store));
        Assert.Equal("Mirador can't tell which Lighthouse space is in use, so it can't export.", RoomSetupExport.Refusal(false, false, steam.Path, null, store));
        Assert.Equal("Mirador has no room setup of this space for SteamVR's Lighthouse tracking. Set it up first.", Refusal());

        // Another origin's setup, or another space's, isn't this one.
        store.Set(new RoomSetup("Pimax", Universe, Lifted, Taken));
        store.Set(new RoomSetup(Origin, "30", Lifted, Taken));
        Assert.Equal("Mirador has no room setup of this space for SteamVR's Lighthouse tracking. Set it up first.", Refusal());
    }

    [Fact]
    public void A_setup_taken_without_a_space_is_the_one_exported()
    {
        store.Set(new RoomSetup(Origin, null, Lifted, Taken));
        Assert.Null(Refusal());

        RoomSetupExport.Run(steam.Path, steam.File("backups"), Universe, store, TimeProvider.System);

        // The space now holds what was in use: Mirador's offset on top of 30's room setup.
        var placed = Placed(Universe);
        var expected = RoomSetupMath.Transform(Lifted, SteamVrRoom.ChaperonePose([0.25f, -1.5f, 0.125f], 1));
        Assert.Equal(expected.X, placed.X, 1e-4f);
        Assert.Equal(expected.Y, placed.Y, 1e-4f);
        Assert.Equal(expected.Z, placed.Z, 1e-4f);
        Assert.Equal(1, MathF.Abs((expected.Qy * placed.Qy) + (expected.Qw * placed.Qw)), 1e-4f);

        // This space now gets no offset from Mirador; the setup taken without a space stays for the others.
        Assert.Equal(new RoomSetup(Origin, null, Lifted, Taken), store.Get(Origin, null));
        var client = new LighthouseClient();
        Assert.Equal(1, RoomSetupApplier.Apply(client, store, Universe));
        Assert.Equal(MonadoPose.Identity, client.Set);
        Assert.Equal(1, RoomSetupApplier.Apply(client, store, "30"));
        Assert.Equal(Lifted, client.Set);
    }

    [Fact]
    public void After_an_export_mirador_applies_nothing_on_top_for_that_space()
    {
        store.Set(new RoomSetup(Origin, Universe, Lifted, Taken));
        store.Set(new RoomSetup(Origin, null, MonadoPose.Identity with { Y = -0.3f }, Taken));

        RoomSetupExport.Run(steam.Path, steam.File("backups"), Universe, store, TimeProvider.System);

        Assert.Equal(MonadoPose.Identity, store.Get(Origin, Universe)!.Offset);
        var client = new LighthouseClient();
        RoomSetupApplier.Apply(client, store, Universe);
        Assert.Equal(MonadoPose.Identity, client.Set);

        // Exporting again writes nothing more on top.
        var once = Placed(Universe);
        RoomSetupExport.Run(steam.Path, steam.File("backups"), Universe, store, TimeProvider.System);
        Assert.Equal(once.Y, Placed(Universe).Y, 1e-5f);
        Assert.Equal(once.X, Placed(Universe).X, 1e-5f);
    }

    /// <summary>Where steamvr_lh's chaperone for <paramref name="universe"/> puts a device at the driver's origin.</summary>
    private MonadoPose Placed(string universe)
    {
        var (translation, yaw) = SteamVrRoom.Standing(File.ReadAllText(SteamVrRoom.ChaperoneInfoPath(steam.Path)), universe)!.Value;
        return SteamVrRoom.ChaperonePose(translation, yaw);
    }

    [Fact]
    public void A_space_without_steamvrs_entry_is_told_its_walls_are_copied()
    {
        const string question = "Mirador will write this room setup into SteamVR's settings. A copy is kept.";
        Assert.Equal(
            question + " SteamVR's play-area walls for this space are copied from another space; run SteamVR's Room Setup to draw them for this room.",
            RoomSetupExport.QuestionFor(steam.Path, Universe));
        Assert.Equal(question, RoomSetupExport.QuestionFor(steam.Path, "30"));
    }

    [Fact]
    public void A_setup_taken_on_another_steamvr_room_setup_isnt_exported()
    {
        // Measured while 30's room setup was still at the origin; SteamVR's Room Setup has moved it since.
        store.Set(new RoomSetup(Origin, Universe, Lifted, Taken, new SteamVrBase("30", 0, 0, 0, 0)));

        Assert.Equal(
            "SteamVR's room setup changed since Mirador's: the floor and forward direction may be wrong in games. Measure again on the Lighthouse page.",
            Refusal());

        store.Set(new RoomSetup(Origin, Universe, Lifted, Taken, new SteamVrBase("30", 0.25f, -1.5f, 0.125f, 1)));
        Assert.Null(Refusal());

        // A setup kept before Mirador noted the base is exported as it is.
        store.Set(new RoomSetup(Origin, Universe, Lifted, Taken));
        Assert.Null(Refusal());
    }

    [Fact]
    public void After_an_export_the_kept_none_notes_the_new_room_setup()
    {
        store.Set(new RoomSetup(Origin, Universe, Lifted, Taken, new SteamVrBase("30", 0.25f, -1.5f, 0.125f, 1)));

        RoomSetupExport.Run(steam.Path, steam.File("backups"), Universe, store, TimeProvider.System);

        var (translation, yaw) = SteamVrRoom.Standing(File.ReadAllText(SteamVrRoom.ChaperoneInfoPath(steam.Path)), Universe)!.Value;
        Assert.Equal(new SteamVrBase(Universe, translation[0], translation[1], translation[2], yaw), store.Get(Origin, Universe)!.Base);
        Assert.False(RoomSetupWarning.BaseChanged(store.All(), Universe, SteamVrRoom.BaseNow(steam.Path), profileUsesSteamVrLh: true));
    }
}
