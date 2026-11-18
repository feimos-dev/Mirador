using Mirador.Core.Lighthouse;

namespace Mirador.Core.Tests.Lighthouse;

public class SteamVrRoomTests
{
    /// <summary>
    /// A lighthousedb.json as SteamVR writes one: universe ids as strings, base station serial numbers as integers (some
    /// above int32), a station list per universe, other keys that have to be ignored. The first universe has no room
    /// setup.
    /// </summary>
    private const string Lighthousedb = """
        {
           "base_stations" : [
              { "base_serial_number" : 439041101, "dynamic_states" : [] }
           ],
           "known_universes" : [
              {
                 "base_stations" : [
                    { "base_serial_number" : 439041101, "pose" : [ 0.0, 0.0, 0.0, 1.0, 0.5, 2.1, -1.3 ] },
                    { "base_serial_number" : 1584364171, "pose" : [ 0.0, 0.0, 0.0, 1.0, -0.5, 2.2, 1.4 ] }
                 ],
                 "id" : "1793836993",
                 "last_modified_timestamp" : 1762900000
              },
              {
                 "base_stations" : [
                    { "base_serial_number" : 48 }
                 ],
                 "id" : "30"
              },
              {
                 "base_stations" : [],
                 "id" : "2"
              },
              {
                 "base_stations" : [
                    { "base_serial_number" : 2618105391 }
                 ],
                 "id" : "1779499028"
              }
           ],
           "revision" : 4
        }
        """;

    /// <summary>A chaperone_info.vrchap as SteamVR writes one: universe ids as strings, other keys that have to be ignored.</summary>
    private const string Vrchap = """
        {
           "jsonid" : "chaperone_info",
           "universes" : [
              {
                 "collision_bounds" : [],
                 "play_area" : [ 2.5, 2.0 ],
                 "seated" : { "translation" : [ 0.0, -1.1, 0.0 ], "yaw" : 0.0 },
                 "standing" : { "translation" : [ 0.1, -1.6, 0.2 ], "yaw" : 1.57 },
                 "universeID" : "2"
              },
              { "standing" : { "translation" : [ 0.0, 0.0, 0.0 ], "yaw" : 0.0 }, "universeID" : "30" },
              { "standing" : { "translation" : [ 0.0, 0.0, 0.0 ], "yaw" : 0.0 }, "universeID" : "31" },
              { "standing" : { "translation" : [ 0.0, 0.0, 0.0 ], "yaw" : 0.0 }, "universeID" : "1779499028" }
           ],
           "version" : 5
        }
        """;

    [Fact]
    public void Universes_keep_file_order_and_station_serials()
    {
        var universes = SteamVrRoom.Universes(Lighthousedb);

        Assert.Equal(["1793836993", "30", "2", "1779499028"], universes.Select(u => u.Id));
        Assert.Equal(["439041101", "1584364171"], universes[0].StationSerials);
        Assert.Equal(["48"], universes[1].StationSerials);
        Assert.Empty(universes[2].StationSerials);
        Assert.Equal(["2618105391"], universes[3].StationSerials);
    }

    [Fact]
    public void Universe_ids_keep_their_written_form()
    {
        var universes = SteamVrRoom.Universes("""
            { "known_universes" : [
                { "id" : 18446744073709551615 },
                { "id" : "0042" },
                { "id" : 1.50 },
                { "id" : null },
                { "id" : [ 1 ] },
                { "base_stations" : [] },
                "not a universe",
                { "id" : 7, "base_stations" : [ { "base_serial_number" : 123 }, { "pose" : [] }, { "base_serial_number" : "LHB-7" } ] }
            ] }
            """);

        Assert.Equal(["18446744073709551615", "0042", "1.50", "7"], universes.Select(u => u.Id));
        Assert.Equal(["123", "LHB-7"], universes[3].StationSerials);
    }

    [Fact]
    public void Serials_above_int32_keep_their_written_form()
    {
        var universes = SteamVrRoom.Universes("""
            { "known_universes" : [ { "id" : "1", "base_stations" : [ { "base_serial_number" : 4294967295 }, { "base_serial_number" : 2147483648 } ] } ] }
            """);

        Assert.Equal(["4294967295", "2147483648"], Assert.Single(universes).StationSerials);
    }

    [Fact]
    public void Room_setups_list_universe_ids()
    {
        Assert.Equal(new HashSet<string> { "2", "30", "31", "1779499028" }, SteamVrRoom.RoomSetups(Vrchap));

        // Numbers are kept in their written form too.
        Assert.Equal(
            new HashSet<string> { "18446744073709551615", "5" },
            SteamVrRoom.RoomSetups("""{ "universes" : [ { "universeID" : 18446744073709551615 }, { "universeID" : "5" }, { "seated" : {} } ] }"""));
    }

    [Fact]
    public void Steamvr_lh_picks_the_first_universe_with_a_room_setup()
    {
        // In the database's order, not the room setups' order: 30 comes before 2 there.
        Assert.Equal("30", SteamVrRoom.SteamVrLhPick(SteamVrRoom.Universes(Lighthousedb), SteamVrRoom.RoomSetups(Vrchap)));
    }

    [Fact]
    public void No_room_setup_gives_no_pick()
    {
        var universes = SteamVrRoom.Universes(Lighthousedb);

        Assert.Null(SteamVrRoom.SteamVrLhPick(universes, new HashSet<string>()));
        Assert.Null(SteamVrRoom.SteamVrLhPick(universes, new HashSet<string> { "31", "99" }));
        Assert.Null(SteamVrRoom.SteamVrLhPick([], SteamVrRoom.RoomSetups(Vrchap)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{ \"known_universes\" : [ { \"id\" : 1 } ")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{ \"known_universes\" : {}, \"universes\" : 7 }")]
    [InlineData("{ \"known_universes\" : [ { \"id\" : 1 } ], \"known_universes\" : [], \"universes\" : [ { \"universeID\" : \"1\" } ], \"universes\" : [] }")]
    public void Malformed_files_give_empty_results(string text)
    {
        Assert.Empty(SteamVrRoom.Universes(text));
        Assert.Empty(SteamVrRoom.RoomSetups(text));
    }

    [Fact]
    public void Paths_are_in_steams_config_folder()
    {
        Assert.Equal(@"C:\Steam\config\lighthouse\lighthousedb.json", SteamVrRoom.LighthousedbPath(@"C:\Steam"));
        Assert.Equal(@"C:\Steam\config\chaperone_info.vrchap", SteamVrRoom.ChaperoneInfoPath(@"C:\Steam"));
    }
}
