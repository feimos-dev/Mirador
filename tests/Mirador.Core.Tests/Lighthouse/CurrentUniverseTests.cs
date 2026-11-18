using Mirador.Core.BaseStations;
using Mirador.Core.Lighthouse;

namespace Mirador.Core.Tests.Lighthouse;

public class CurrentUniverseTests
{
    // 0xDEADBEEF, 0x0BADF00D, 0x1234ABCD, 0x00C0FFEE as SteamVR writes base_serial_number: decimal integers.
    private const string DeadBeef = "3735928559";
    private const string BadFood = "195948557";
    private const string Abcd = "305441741";
    private const string Coffee = "12648430";

    /// <summary>
    /// The universes of a lighthousedb.json: the first holds two stations and has no room setup, the others have one.
    /// <c>steamvr_lh</c> picks <c>30</c>.
    /// </summary>
    private static readonly IReadOnlyList<LighthouseUniverse> Universes = SteamVrRoom.Universes($$"""
        {
           "known_universes" : [
              {
                 "base_stations" : [ { "base_serial_number" : {{DeadBeef}} }, { "base_serial_number" : {{BadFood}} } ],
                 "id" : "1793836993"
              },
              { "base_stations" : [ { "base_serial_number" : {{Abcd}} } ], "id" : "30" },
              { "base_stations" : [ { "base_serial_number" : {{Coffee}} }, { "base_serial_number" : {{DeadBeef}} } ], "id" : "2" },
              { "base_stations" : [], "id" : "1779499028" }
           ]
        }
        """);

    private static readonly IReadOnlySet<string> RoomSetups = new HashSet<string> { "2", "30", "31", "1779499028" };

    private static KnownStation V2(string hex) => new("AA:BB:CC:DD:EE:" + hex[^2..], "LHB-" + hex);

    private static KnownStation V1(string id) => new("11:22:33:44:55:66", "HTC BS 123456", Id: id);

    [Fact]
    public void The_universe_holding_the_known_stations_is_current()
    {
        Assert.Equal(new UniversePick("30", false), CurrentUniverse.Pick(Universes, RoomSetups, [V2("1234ABCD")]));

        // Both stations are only in 1793836993; one of them is in 2 as well.
        Assert.Equal("1793836993", CurrentUniverse.Pick(Universes, RoomSetups, [V2("DEADBEEF"), V2("0BADF00D")]).Id);

        // Names compare case-insensitively.
        Assert.Equal("1793836993", CurrentUniverse.Pick(Universes, RoomSetups, [V2("deadbeef"), V2("0badf00d")]).Id);

        // A station in no universe (a new one SteamVR never saw) doesn't count.
        Assert.Equal("2", CurrentUniverse.Pick(Universes, RoomSetups, [V2("00C0FFEE"), V2("FFFFFFFF")]).Id);
    }

    [Fact]
    public void No_known_stations_falls_back_to_steamvr_lh()
    {
        Assert.Equal(new UniversePick("30", false), CurrentUniverse.Pick(Universes, RoomSetups, []));

        // Only stations SteamVR never saw, or names that aren't a second-generation station's.
        Assert.Equal(new UniversePick("30", false), CurrentUniverse.Pick(Universes, RoomSetups, [V2("FFFFFFFF")]));
        Assert.Equal(
            new UniversePick("30", false),
            CurrentUniverse.Pick(Universes, RoomSetups, [new("AA:BB:CC:DD:EE:01", "LHB-DEADBEE"), new("AA:BB:CC:DD:EE:02", "LHB-DEADBEEFF"), new("AA:BB:CC:DD:EE:03", "LHB-DEADBEEG")]));

        // No room setup at all: no pick either way.
        Assert.Equal(new UniversePick(null, false), CurrentUniverse.Pick(Universes, new HashSet<string>(), []));
    }

    [Fact]
    public void First_generation_stations_fall_back_to_steamvr_lh()
    {
        // Their 8-hex ID isn't the serial number SteamVR writes, so it is never matched, even when the digits agree.
        Assert.Equal(new UniversePick("30", false), CurrentUniverse.Pick(Universes, RoomSetups, [V1("DEADBEEF"), V1("0BADF00D")]));

        // Next to a second-generation station, only that one counts.
        Assert.Equal(new UniversePick("2", true), CurrentUniverse.Pick(Universes, RoomSetups, [V1("DEADBEEF"), V2("00C0FFEE")]));
    }

    [Fact]
    public void Two_matching_universes_fall_back_to_steamvr_lh()
    {
        // DEADBEEF is in 1793836993 and in 2.
        Assert.Equal(new UniversePick("30", false), CurrentUniverse.Pick(Universes, RoomSetups, [V2("DEADBEEF")]));

        // No universe holds both.
        Assert.Equal(new UniversePick("30", false), CurrentUniverse.Pick(Universes, RoomSetups, [V2("0BADF00D"), V2("00C0FFEE")]));
    }

    [Fact]
    public void A_current_universe_other_than_steamvr_lhs_is_flagged()
    {
        Assert.Equal(
            new UniversePick("1793836993", true),
            CurrentUniverse.Pick(Universes, RoomSetups, [V2("DEADBEEF"), V2("0BADF00D")]));

        // Without any room setup SteamVR's isn't used, so there is nothing to flag.
        Assert.Equal(
            new UniversePick("1793836993", false),
            CurrentUniverse.Pick(Universes, new HashSet<string>(), [V2("DEADBEEF"), V2("0BADF00D")]));
    }
}
