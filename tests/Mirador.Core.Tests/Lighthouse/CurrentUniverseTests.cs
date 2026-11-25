using Mirador.Core.BaseStations;
using Mirador.Core.Lighthouse;

namespace Mirador.Core.Tests.Lighthouse;

public class CurrentUniverseTests
{
    private const string DeadBeef = "3735928559";
    private const string BadFood = "195948557";
    private const string Abcd = "305441741";
    private const string Coffee = "12648430";

    private static readonly IReadOnlyList<LighthouseUniverse> Universes = SteamVrRoom.Universes($$"""
        {
           "known_universes" : [
              {
                 "base_stations" : [ { "base_serial_number" : {{DeadBeef}} }, { "base_serial_number" : {{BadFood}} } ],
                 "id" : "1500000001"
              },
              { "base_stations" : [ { "base_serial_number" : {{Abcd}} } ], "id" : "30" },
              { "base_stations" : [ { "base_serial_number" : {{Coffee}} }, { "base_serial_number" : {{DeadBeef}} } ], "id" : "2" },
              { "base_stations" : [], "id" : "1500000004" }
           ]
        }
        """);

    private static readonly IReadOnlySet<string> RoomSetups = new HashSet<string> { "2", "30", "31", "1500000004" };

    private static KnownStation V2(string hex) => new("AA:BB:CC:DD:EE:" + hex[^2..], "LHB-" + hex);

    private static KnownStation V1(string id) => new("11:22:33:44:55:66", "HTC BS 123456", Id: id);

    [Fact]
    public void The_universe_holding_the_known_stations_is_current()
    {
        Assert.Equal(new UniversePick("30", false), CurrentUniverse.Pick(Universes, RoomSetups, [V2("1234ABCD")]));

        Assert.Equal("1500000001", CurrentUniverse.Pick(Universes, RoomSetups, [V2("DEADBEEF"), V2("0BADF00D")]).Id);

        Assert.Equal("1500000001", CurrentUniverse.Pick(Universes, RoomSetups, [V2("deadbeef"), V2("0badf00d")]).Id);

        Assert.Equal("2", CurrentUniverse.Pick(Universes, RoomSetups, [V2("00C0FFEE"), V2("FFFFFFFF")]).Id);
    }

    [Fact]
    public void No_known_stations_falls_back_to_steamvr_lh()
    {
        Assert.Equal(new UniversePick("30", false), CurrentUniverse.Pick(Universes, RoomSetups, []));

        Assert.Equal(new UniversePick("30", false), CurrentUniverse.Pick(Universes, RoomSetups, [V2("FFFFFFFF")]));
        Assert.Equal(
            new UniversePick("30", false),
            CurrentUniverse.Pick(Universes, RoomSetups, [new("AA:BB:CC:DD:EE:01", "LHB-DEADBEE"), new("AA:BB:CC:DD:EE:02", "LHB-DEADBEEFF"), new("AA:BB:CC:DD:EE:03", "LHB-DEADBEEG")]));

        Assert.Equal(new UniversePick(null, false), CurrentUniverse.Pick(Universes, new HashSet<string>(), []));
    }

    [Fact]
    public void First_generation_stations_fall_back_to_steamvr_lh()
    {
        Assert.Equal(new UniversePick("30", false), CurrentUniverse.Pick(Universes, RoomSetups, [V1("DEADBEEF"), V1("0BADF00D")]));

        Assert.Equal(new UniversePick("2", true), CurrentUniverse.Pick(Universes, RoomSetups, [V1("DEADBEEF"), V2("00C0FFEE")]));
    }

    [Fact]
    public void Two_matching_universes_fall_back_to_steamvr_lh()
    {
        Assert.Equal(new UniversePick("30", false), CurrentUniverse.Pick(Universes, RoomSetups, [V2("DEADBEEF")]));

        Assert.Equal(new UniversePick("30", false), CurrentUniverse.Pick(Universes, RoomSetups, [V2("0BADF00D"), V2("00C0FFEE")]));
    }

    [Fact]
    public void Steams_files_are_read_from_its_config_folder()
    {
        using var steam = new Mirador.Tests.TempDir();
        Assert.Equal(new UniversePick(null, false), CurrentUniverse.FromSteam(null, [V2("DEADBEEF")]));
        Assert.Equal(new UniversePick(null, false), CurrentUniverse.FromSteam(steam.Path, [V2("DEADBEEF")]));

        Directory.CreateDirectory(steam.File("config", "lighthouse"));
        File.WriteAllText(steam.File("config", "lighthouse", "lighthousedb.json"), $$"""
            { "known_universes" : [
                { "base_stations" : [ { "base_serial_number" : {{DeadBeef}} } ], "id" : "1500000001" },
                { "base_stations" : [ { "base_serial_number" : {{Abcd}} } ], "id" : "30" } ] }
            """);

        Assert.Equal(new UniversePick("1500000001", false), CurrentUniverse.FromSteam(steam.Path, [V2("DEADBEEF")]));

        File.WriteAllText(steam.File("config", "chaperone_info.vrchap"), """{ "universes" : [ { "universeID" : "30" } ] }""");
        Assert.Equal(new UniversePick("1500000001", true), CurrentUniverse.FromSteam(steam.Path, [V2("DEADBEEF")]));
        Assert.Equal(new UniversePick("30", false), CurrentUniverse.FromSteam(steam.Path, []));
    }

    [Fact]
    public void A_current_universe_other_than_steamvr_lhs_is_flagged()
    {
        Assert.Equal(
            new UniversePick("1500000001", true),
            CurrentUniverse.Pick(Universes, RoomSetups, [V2("DEADBEEF"), V2("0BADF00D")]));

        Assert.Equal(
            new UniversePick("1500000001", false),
            CurrentUniverse.Pick(Universes, new HashSet<string>(), [V2("DEADBEEF"), V2("0BADF00D")]));
    }
}
