using Mirador.Core.BaseStations;
using Mirador.Core.Lighthouse;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Playspace;
using Mirador.Tests;

namespace Mirador.Core.Tests.Playspace;

public class RoomSetupWarningTests
{
    private const string NoSetup =
        "No room setup for this space: the floor and forward direction may be wrong in games. Set it up on the Lighthouse page.";

    private const string OtherSpace =
        "SteamVR's room setup of another space is used: the floor and forward direction may be wrong in games. Set it up on the Lighthouse page.";

    private static readonly DateTimeOffset Taken = new(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);

    private static readonly IReadOnlySet<string> SteamVrSetups = new HashSet<string> { "2", "30", "31", "1500000004" };

    [Fact]
    public void Mirador_having_a_setup_gives_no_warning()
    {
        Assert.Null(RoomSetupWarning.For(new UniversePick("1500000001", false), SteamVrSetups, miradorHasSetup: true, profileUsesSteamVrLh: true));
        Assert.Null(RoomSetupWarning.For(new UniversePick("1500000001", true), SteamVrSetups, miradorHasSetup: true, profileUsesSteamVrLh: true));
        Assert.Null(RoomSetupWarning.For(new UniversePick(null, false), new HashSet<string>(), miradorHasSetup: true, profileUsesSteamVrLh: false));
    }

    [Fact]
    public void Steamvr_lhs_room_setup_of_this_space_gives_no_warning()
    {
        Assert.Null(RoomSetupWarning.For(new UniversePick("30", false), SteamVrSetups, miradorHasSetup: false, profileUsesSteamVrLh: true));
    }

    [Fact]
    public void No_setup_warns()
    {
        Assert.Equal(NoSetup, RoomSetupWarning.For(new UniversePick("99", false), SteamVrSetups, false, true));
        Assert.Equal(NoSetup, RoomSetupWarning.For(new UniversePick(null, false), SteamVrSetups, false, true));

        // SteamVR's file counts only when Monado's steamvr_lh driver reads it.
        Assert.Equal(NoSetup, RoomSetupWarning.For(new UniversePick("30", false), SteamVrSetups, false, false));
    }

    [Fact]
    public void Another_spaces_steamvr_setup_in_use_warns()
    {
        Assert.Equal(OtherSpace, RoomSetupWarning.For(new UniversePick("1500000001", true), SteamVrSetups, false, true));

        // Even when this space has an entry too: steamvr_lh takes the first one it finds.
        Assert.Equal(OtherSpace, RoomSetupWarning.For(new UniversePick("31", true), SteamVrSetups, false, true));

        // Without steamvr_lh no SteamVR room setup is used at all.
        Assert.Equal(NoSetup, RoomSetupWarning.For(new UniversePick("1500000001", true), SteamVrSetups, false, false));
    }

    [Fact]
    public void A_space_without_its_own_setup_after_another_with_one_warns()
    {
        // Universe 1500000001 holds the stations Mirador knows and has no SteamVR room setup; the others have one.
        var universes = SteamVrRoom.Universes("""
            { "known_universes" : [
                { "base_stations" : [ { "base_serial_number" : 3735928559 } ], "id" : "1500000001" },
                { "base_stations" : [], "id" : "2" },
                { "base_stations" : [], "id" : "30" },
                { "base_stations" : [], "id" : "31" },
                { "base_stations" : [], "id" : "1500000004" } ] }
            """);
        var pick = CurrentUniverse.Pick(universes, SteamVrSetups, [new KnownStation("AA:BB:CC:DD:EE:01", "LHB-DEADBEEF")]);

        Assert.Equal(new UniversePick("1500000001", true), pick);
        Assert.Equal(OtherSpace, RoomSetupWarning.For(pick, SteamVrSetups, miradorHasSetup: false, profileUsesSteamVrLh: true));
    }

    [Fact]
    public void Mirador_has_a_setup_for_the_space_or_one_taken_without_a_space()
    {
        RoomSetup Setup(string origin, string? universe) => new(origin, universe, MonadoPose.Identity, Taken);

        Assert.True(RoomSetupWarning.HasSetup([Setup("steamvr_lh", "1500000001")], "1500000001"));
        Assert.True(RoomSetupWarning.HasSetup([Setup("simulated", null)], "1500000001"));
        Assert.True(RoomSetupWarning.HasSetup([Setup("steamvr_lh", null)], null));
        Assert.False(RoomSetupWarning.HasSetup([Setup("steamvr_lh", "30")], "1500000001"));
        Assert.False(RoomSetupWarning.HasSetup([Setup("steamvr_lh", "30")], null));
        Assert.False(RoomSetupWarning.HasSetup([], "1500000001"));
    }

    private const string Changed =
        "SteamVR's room setup changed since Mirador's: the floor and forward direction may be wrong in games. Measure again on the Lighthouse page.";

    [Fact]
    public void A_setup_taken_on_another_steamvr_room_setup_is_noticed()
    {
        const string origin = SteamVrRoom.LighthouseOrigin;
        var base30 = new SteamVrBase("30", 0.25f, -1.5f, 0.125f, 1);
        RoomSetup[] setups = [new(origin, "1500000001", MonadoPose.Identity, Taken, base30)];

        Assert.False(RoomSetupWarning.BaseChanged(setups, "1500000001", base30, profileUsesSteamVrLh: true));
        Assert.True(RoomSetupWarning.BaseChanged(setups, "1500000001", base30 with { Y = -1.2f }, true));
        Assert.True(RoomSetupWarning.BaseChanged(setups, "1500000001", SteamVrBase.None, true));
        Assert.Equal(Changed, RoomSetupWarning.SteamVrChanged);

        // Not with another driver than steamvr_lh, nor without Steam's files.
        Assert.False(RoomSetupWarning.BaseChanged(setups, "1500000001", base30 with { Y = -1.2f }, false));
        Assert.False(RoomSetupWarning.BaseChanged(setups, "1500000001", null, true));

        // A setup without a base, or another origin's, says nothing.
        Assert.False(RoomSetupWarning.BaseChanged([new(origin, "1500000001", MonadoPose.Identity, Taken)], "1500000001", base30, true));
        Assert.False(RoomSetupWarning.BaseChanged([new("Pimax", "1500000001", MonadoPose.Identity, Taken, base30)], "1500000001", SteamVrBase.None, true));

        // The setup that applies: one taken without a space counts too.
        Assert.True(RoomSetupWarning.BaseChanged([new(origin, null, MonadoPose.Identity, Taken, base30)], "1500000001", SteamVrBase.None, true));
    }

    [Fact]
    public void The_warning_is_read_from_steams_files()
    {
        using var steam = new TempDir();
        Directory.CreateDirectory(steam.File("config", "lighthouse"));
        File.WriteAllText(steam.File("config", "lighthouse", "lighthousedb.json"), """
            { "known_universes" : [
                { "base_stations" : [ { "base_serial_number" : 3735928559 } ], "id" : "1500000001" },
                { "base_stations" : [], "id" : "30" } ] }
            """);
        File.WriteAllText(steam.File("config", "chaperone_info.vrchap"), """{ "universes" : [ { "universeID" : "30" } ] }""");
        KnownStation[] stations = [new("AA:BB:CC:DD:EE:01", "LHB-DEADBEEF")];

        Assert.Equal(OtherSpace, RoomSetupWarning.FromSteam(steam.Path, stations, [], profileUsesSteamVrLh: true));
        Assert.Null(RoomSetupWarning.FromSteam(steam.Path, [], [], profileUsesSteamVrLh: true));
        Assert.Null(RoomSetupWarning.FromSteam(steam.Path, stations, [new RoomSetup("steamvr_lh", "1500000001", MonadoPose.Identity, Taken)], true));

        // No Steam: only Mirador's own setup counts.
        Assert.Equal(NoSetup, RoomSetupWarning.FromSteam(null, stations, [], profileUsesSteamVrLh: true));
        Assert.Null(RoomSetupWarning.FromSteam(null, stations, [new RoomSetup("steamvr_lh", null, MonadoPose.Identity, Taken)], true));
    }
}
