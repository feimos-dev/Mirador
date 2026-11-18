using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Mirador.Core.Lighthouse;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Playspace;
using Mirador.Tests;

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

    /// <summary>A chaperone_info.vrchap shaped as SteamVR writes one: bounds, play area, seated, standing, time, id.</summary>
    private const string TwoSpaces = """
        {
           "jsonid" : "chaperone_info",
           "universes" : [
              {
                 "collision_bounds" : [ [ [ 1.0, 0.0, 1.0 ], [ 1.0, 2.43, 1.0 ], [ -1.0, 2.43, 1.0 ], [ -1.0, 0.0, 1.0 ] ] ],
                 "play_area" : [ 2, 2 ],
                 "seated" : { "translation" : [ 0, 0, 0 ], "yaw" : 0 },
                 "standing" : { "translation" : [ 0.25, -1.5, 0.125 ], "yaw" : 1 },
                 "time" : "Tue Nov 18 09:30:00 2026",
                 "universeID" : "30"
              },
              {
                 "collision_bounds" : [],
                 "play_area" : [ 3, 3 ],
                 "seated" : { "translation" : [ 0, 0, 0 ], "yaw" : 0 },
                 "standing" : { "translation" : [ 0, 0, 0 ], "yaw" : 0 },
                 "time" : "Tue Nov 18 09:31:00 2026",
                 "universeID" : "2"
              }
           ],
           "version" : 5
        }
        """;

    private static JsonObject Entry(string json, string universe) =>
        JsonNode.Parse(json)!["universes"]!.AsArray().OfType<JsonObject>().Single(u => (string?)u["universeID"] == universe);

    [Fact]
    public void With_standing_updates_only_that_universe()
    {
        var written = SteamVrRoom.WithStanding(TwoSpaces, "2", [0.5f, -1.25f, -0.75f], 0.5f);

        var two = Entry(written, "2");
        Assert.Equal([0.5f, -1.25f, -0.75f], two["standing"]!["translation"]!.AsArray().Select(n => (float)n!));
        Assert.Equal(0.5f, (float)two["standing"]!["yaw"]!);
        Assert.Equal([3, 3], two["play_area"]!.AsArray().Select(n => (int)n!));
        Assert.Equal("Tue Nov 18 09:31:00 2026", (string?)two["time"]);

        // The other universe and everything around it as they were.
        Assert.True(JsonNode.DeepEquals(Entry(TwoSpaces, "30"), Entry(written, "30")));
        var root = JsonNode.Parse(written)!;
        Assert.Equal("chaperone_info", (string?)root["jsonid"]);
        Assert.Equal(5, (int)root["version"]!);
        Assert.Equal(2, root["universes"]!.AsArray().Count);
    }

    [Fact]
    public void With_standing_adds_a_missing_universe_from_an_existing_entry()
    {
        var written = SteamVrRoom.WithStanding(TwoSpaces, "1793836993", [0, -0.1f, 0], -1.5f);

        var added = Entry(written, "1793836993");
        Assert.Equal([0f, -0.1f, 0f], added["standing"]!["translation"]!.AsArray().Select(n => (float)n!));
        Assert.Equal(-1.5f, (float)added["standing"]!["yaw"]!);

        // A copy of the first entry with only its id and standing changed.
        var first = Entry(TwoSpaces, "30").DeepClone().AsObject();
        first["universeID"] = "1793836993";
        first["standing"] = added["standing"]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(first, added));
        Assert.Equal(3, JsonNode.Parse(written)!["universes"]!.AsArray().Count);
        Assert.True(JsonNode.DeepEquals(Entry(TwoSpaces, "30"), Entry(written, "30")));
    }

    [Theory]
    [InlineData("""{ "jsonid" : "chaperone_info", "universes" : [], "version" : 5 }""")]
    [InlineData("""{ "jsonid" : "chaperone_info", "version" : 5 }""")]
    [InlineData("""{ "universes" : [ 7, null ] }""")]
    public void With_standing_without_any_entry_fails_with_the_message(string vrchap)
    {
        var e = Assert.Throws<MiradorException>(() => SteamVrRoom.WithStanding(vrchap, "1793836993", [0, 0, 0], 0));
        Assert.Equal("SteamVR has no room setup to copy; run SteamVR's Room Setup once.", e.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "universes" : [], "universes" : [] }""")]
    public void A_file_that_isnt_steamvrs_is_left_alone(string vrchap)
    {
        var e = Assert.Throws<MiradorException>(() => SteamVrRoom.WithStanding(vrchap, "2", [0, 0, 0], 0));
        Assert.Equal("SteamVR's room setup file isn't one Mirador can read, so Mirador leaves it as it is.", e.Message);
    }

    [Fact]
    public void Standing_is_read_as_steamvr_lh_reads_it()
    {
        Assert.Equal("0.25 -1.5 0.125 / 1", Standing(TwoSpaces, "30"));
        Assert.Null(SteamVrRoom.Standing(TwoSpaces, "1793836993"));

        // Missing numbers count as 0, as in the driver.
        Assert.Equal("0.5 0 0 / 0", Standing("""{ "universes" : [ { "universeID" : "9", "standing" : { "translation" : [ 0.5 ] } } ] }""", "9"));
    }

    /// <summary>The standing as "x y z / yaw", for comparing.</summary>
    private static string Standing(string json, string universe)
    {
        var (translation, yaw) = SteamVrRoom.Standing(json, universe)!.Value;
        return FormattableString.Invariant($"{string.Join(' ', translation.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)))} / {yaw}");
    }

    [Fact]
    public void The_chaperone_pose_is_steamvr_lhs()
    {
        // init_chaperone: orientation = yaw about (0, −1, 0); position = translation turned by that orientation.
        // A yaw of π/2 is a quarter turn clockwise seen from above, so +X goes to +Z.
        var pose = SteamVrRoom.ChaperonePose([1, 0, 0], MathF.PI / 2);

        Assert.Equal(0, pose.Qx, 1e-5f);
        Assert.Equal(-MathF.Sqrt(0.5f), pose.Qy, 1e-5f);
        Assert.Equal(0, pose.Qz, 1e-5f);
        Assert.Equal(MathF.Sqrt(0.5f), pose.Qw, 1e-5f);
        Assert.Equal(0, pose.X, 1e-5f);
        Assert.Equal(0, pose.Y, 1e-5f);
        Assert.Equal(1, pose.Z, 1e-5f);
    }

    [Theory]
    [InlineData(0.3f, -0.12f, 0f, 0f, 0f, 0f)]
    [InlineData(-2.4f, 0.05f, 0.25f, -1.5f, 0.125f, 1f)]
    [InlineData(1.1f, -1.6f, -0.4f, 0.2f, 0.9f, -2.9f)]
    public void The_exported_offset_reproduces_the_applied_one(float offsetYaw, float offsetY, float tx, float ty, float tz, float yaw)
    {
        // Mirador applied its offset on top of SteamVR's standing: device in stage = offset * chaperone * device in the driver.
        var offset = RoomSetupMath.Correct(MonadoPose.Identity with { X = 0.3f, Z = -0.2f }, -offsetY, -offsetYaw);
        float[] before = [tx, ty, tz];
        var (translation, newYaw) = SteamVrRoom.StandingFor(offset, before, yaw);

        // After the export Mirador's offset is gone and steamvr_lh applies the new standing alone.
        MonadoPose[] devices = [MonadoPose.Identity, new(0.1f, 0.6f, 0.05f, 0.79f, -0.3f, 1.6f, 0.2f), new(0, 0, 0, 1, 1, 0.05f, -2)];
        foreach (var raw in devices)
        {
            var applied = RoomSetupMath.Transform(offset, RoomSetupMath.Transform(SteamVrRoom.ChaperonePose(before, yaw), raw));
            var exported = RoomSetupMath.Transform(SteamVrRoom.ChaperonePose(translation, newYaw), raw);
            Assert.Equal(applied.X, exported.X, 1e-4f);
            Assert.Equal(applied.Y, exported.Y, 1e-4f);
            Assert.Equal(applied.Z, exported.Z, 1e-4f);
            var dot = (applied.Qx * exported.Qx) + (applied.Qy * exported.Qy) + (applied.Qz * exported.Qz) + (applied.Qw * exported.Qw);
            Assert.Equal(1, MathF.Abs(dot), 1e-4f);
        }
    }

    [Fact]
    public void An_offset_that_tilts_the_space_cant_be_exported()
    {
        var tilted = new MonadoPose(MathF.Sin(0.1f), 0, 0, MathF.Cos(0.1f), 0, 0, 0);

        var e = Assert.Throws<MiradorException>(() => SteamVrRoom.StandingFor(tilted, [0, 0, 0], 0));
        Assert.Equal("This room setup tilts the space, which SteamVR's room setup can't hold.", e.Message);
    }

    [Fact]
    public void Export_keeps_a_copy_writes_the_file_then_clears_miradors_own()
    {
        using var steam = new TempDir();
        var file = steam.File("config", "chaperone_info.vrchap");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, TwoSpaces);
        KnownUniverses(steam, "30", "2");
        var backups = steam.File("Mirador", "backups", "steamvr");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.Zero));
        var offset = RoomSetupMath.Correct(MonadoPose.Identity, -0.1f, 0);
        var cleared = 0;

        SteamVrRoom.Export(steam.Path, backups, "30", offset, () => cleared++, time);

        Assert.Equal(1, cleared);
        Assert.Equal(TwoSpaces, File.ReadAllText(Assert.Single(Directory.GetFiles(backups))));
        Assert.StartsWith("chaperone_info.vrchap.mirador-", Path.GetFileName(Directory.GetFiles(backups)[0]));
        var (translation, yaw) = SteamVrRoom.Standing(File.ReadAllText(file), "30")!.Value;
        Assert.Equal(1, yaw, 1e-5f);
        Assert.Equal(-1.4f, RoomSetupMath.Transform(SteamVrRoom.ChaperonePose(translation, yaw), MonadoPose.Identity).Y, 1e-4f);
        Assert.Equal([file], Directory.GetFiles(Path.GetDirectoryName(file)!));
    }

    [Fact]
    public void A_failed_clear_puts_steamvrs_file_back()
    {
        using var steam = new TempDir();
        var file = steam.File("config", "chaperone_info.vrchap");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, TwoSpaces);
        KnownUniverses(steam, "30", "2");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.Zero));

        var e = Assert.Throws<MiradorException>(() => SteamVrRoom.Export(
            steam.Path, steam.File("backups"), "30", MonadoPose.Identity with { Y = 0.1f }, () => throw new IOException("The disk is full."), time));

        Assert.Equal("Mirador couldn't clear its own copy of the room setup (The disk is full), so it put SteamVR's room setup back. Nothing changed.", e.Message);
        Assert.Equal(TwoSpaces, File.ReadAllText(file));
    }

    [Fact]
    public void Export_without_steamvrs_file_fails_with_the_message()
    {
        using var steam = new TempDir();
        var cleared = false;

        var e = Assert.Throws<MiradorException>(() => SteamVrRoom.Export(
            steam.Path, steam.File("backups"), "30", MonadoPose.Identity, () => cleared = true, TimeProvider.System));

        Assert.Equal("SteamVR has no room setup to copy; run SteamVR's Room Setup once.", e.Message);
        Assert.False(cleared);
        Assert.False(File.Exists(SteamVrRoom.ChaperoneInfoPath(steam.Path)));
    }

    /// <summary>A lighthousedb.json listing <paramref name="ids"/> in that order.</summary>
    private static void KnownUniverses(TempDir steam, params string[] ids)
    {
        var path = SteamVrRoom.LighthousedbPath(steam.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var universes = string.Join(", ", ids.Select(id => $$"""{ "base_stations" : [], "id" : "{{id}}" }"""));
        File.WriteAllText(path, $$"""{ "known_universes" : [ {{universes}} ] }""");
    }

    private static TempDir SteamWith(string vrchap, params string[] known)
    {
        var steam = new TempDir();
        var file = SteamVrRoom.ChaperoneInfoPath(steam.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, vrchap);
        KnownUniverses(steam, known);
        return steam;
    }

    [Fact]
    public void An_export_that_makes_steamvr_use_this_space_goes_ahead()
    {
        // The space in use is listed first and has no entry: steamvr_lh falls back to 30 now, and takes it after the export.
        using var steam = SteamWith(TwoSpaces, "1793836993", "30", "2");
        var (universes, roomSetups) = SteamVrRoom.Read(steam.Path);
        Assert.Equal("30", SteamVrRoom.SteamVrLhPick(universes, roomSetups));
        Assert.Null(SteamVrRoom.ExportRefusal(steam.Path, "1793836993"));
        var cleared = false;

        SteamVrRoom.Export(steam.Path, steam.File("backups"), "1793836993", MonadoPose.Identity with { Y = 0.1f }, () => cleared = true, TimeProvider.System);

        Assert.True(cleared);
        (universes, roomSetups) = SteamVrRoom.Read(steam.Path);
        Assert.Equal("1793836993", SteamVrRoom.SteamVrLhPick(universes, roomSetups));
    }

    [Fact]
    public void An_export_builds_on_the_room_setup_steamvr_uses_now()
    {
        // The space in use has no entry; steamvr_lh applies 30's (translation [0.25, −1.5, 0.125], yaw 1) now, and Mirador's
        // offset was measured on top of that. After the export steamvr_lh applies the new entry alone.
        using var steam = SteamWith(TwoSpaces, "1793836993", "30", "2");
        var (usedTranslation, usedYaw) = SteamVrRoom.Standing(TwoSpaces, "30")!.Value;
        var offset = RoomSetupMath.Correct(MonadoPose.Identity, -0.08f, 0.4f);

        SteamVrRoom.Export(steam.Path, steam.File("backups"), "1793836993", offset, () => { }, TimeProvider.System);

        var (translation, yaw) = SteamVrRoom.Standing(File.ReadAllText(SteamVrRoom.ChaperoneInfoPath(steam.Path)), "1793836993")!.Value;
        MonadoPose[] devices = [MonadoPose.Identity, new(0.1f, 0.6f, 0.05f, 0.79f, -0.3f, 1.6f, 0.2f), new(0, 0, 0, 1, 1, 0.05f, -2)];
        foreach (var raw in devices)
        {
            var before = RoomSetupMath.Transform(offset, RoomSetupMath.Transform(SteamVrRoom.ChaperonePose(usedTranslation, usedYaw), raw));
            var after = RoomSetupMath.Transform(SteamVrRoom.ChaperonePose(translation, yaw), raw);
            Assert.Equal(before.X, after.X, 1e-4f);
            Assert.Equal(before.Y, after.Y, 1e-4f);
            Assert.Equal(before.Z, after.Z, 1e-4f);
            var dot = (before.Qx * after.Qx) + (before.Qy * after.Qy) + (before.Qz * after.Qz) + (before.Qw * after.Qw);
            Assert.Equal(1, MathF.Abs(dot), 1e-4f);
        }

        // 30's own entry is as it was.
        Assert.Equal(TwoSpacesEntry30(), Entry(File.ReadAllText(SteamVrRoom.ChaperoneInfoPath(steam.Path)), "30").ToJsonString());
    }

    private static string TwoSpacesEntry30() => Entry(TwoSpaces, "30").ToJsonString();

    [Fact]
    public void An_export_steamvr_still_wouldnt_use_is_refused()
    {
        // 30 is listed before the space in use and has an entry: steamvr_lh would still take 30.
        using var steam = SteamWith(TwoSpaces, "30", "1793836993", "2");
        const string refusal =
            "SteamVR's room setup of another space is used: the floor and forward direction may be wrong in games. Exporting wouldn't change that: SteamVR would still use the room setup of an earlier space.";
        Assert.Equal(refusal, SteamVrRoom.ExportRefusal(steam.Path, "1793836993"));
        var cleared = false;

        var e = Assert.Throws<MiradorException>(() => SteamVrRoom.Export(
            steam.Path, steam.File("backups"), "1793836993", MonadoPose.Identity with { Y = 0.1f }, () => cleared = true, TimeProvider.System));

        Assert.Equal(refusal, e.Message);
        Assert.False(cleared);
        Assert.Equal(TwoSpaces, File.ReadAllText(SteamVrRoom.ChaperoneInfoPath(steam.Path)));
        Assert.False(Directory.Exists(steam.File("backups")));

        // A space SteamVR doesn't know at all is refused the same way.
        Assert.Equal(refusal, SteamVrRoom.ExportRefusal(steam.Path, "99"));
    }

    [Fact]
    public void A_file_changed_meanwhile_isnt_put_back()
    {
        using var steam = SteamWith(TwoSpaces, "30", "2");
        var file = SteamVrRoom.ChaperoneInfoPath(steam.Path);
        const string changed = """{ "universes" : [] }""";

        var e = Assert.Throws<MiradorException>(() => SteamVrRoom.Export(
            steam.Path, steam.File("backups"), "30", MonadoPose.Identity with { Y = 0.1f },
            () =>
            {
                File.WriteAllText(file, changed);
                throw new IOException("The disk is full.");
            },
            TimeProvider.System));

        Assert.StartsWith(
            "Mirador couldn't clear its own copy of the room setup (The disk is full). SteamVR's room setup changed meanwhile, so Mirador left it as it is: both may apply when Monado next starts. SteamVR's room setup as it was is in ",
            e.Message);
        Assert.Equal(changed, File.ReadAllText(file));
        Assert.Equal([file], Directory.GetFiles(Path.GetDirectoryName(file)!));
    }
}
