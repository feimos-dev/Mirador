using Mirador.Core.BaseStations;
using Mirador.Core.Lighthouse;
using Mirador.Tests;

namespace Mirador.Core.Tests.Lighthouse;

public sealed class SteamVrNowTests : IDisposable
{
    private const string Lighthousedb = """
        { "known_universes" : [
            { "base_stations" : [ { "base_serial_number" : 3735928559 } ], "id" : "1500000001" },
            { "base_stations" : [], "id" : "30" } ] }
        """;

    private const string Vrchap = """
        { "universes" : [ { "standing" : { "translation" : [ 0.25, -1.5, 0.125 ], "yaw" : 1 }, "universeID" : "30" } ] }
        """;

    private static readonly KnownStation[] Stations = [new("AA:BB:CC:DD:EE:01", "LHB-DEADBEEF")];

    private readonly TempDir steam = new();

    public void Dispose() => steam.Dispose();

    private void Write(string? lighthousedb, string? vrchap)
    {
        if (lighthousedb is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SteamVrRoom.LighthousedbPath(steam.Path))!);
            File.WriteAllText(SteamVrRoom.LighthousedbPath(steam.Path), lighthousedb);
        }

        if (vrchap is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SteamVrRoom.ChaperoneInfoPath(steam.Path))!);
            File.WriteAllText(SteamVrRoom.ChaperoneInfoPath(steam.Path), vrchap);
        }
    }

    [Fact]
    public void The_base_is_the_room_setup_steamvr_lh_applies_now()
    {
        Write(Lighthousedb, Vrchap);

        var now = SteamVrNow.Read(steam.Path, Stations);

        // The space in use is 1500000001; steamvr_lh applies 30's room setup.
        Assert.Equal(new UniversePick("1500000001", true), now.Pick);
        Assert.Equal(new HashSet<string> { "30" }, now.RoomSetups);
        Assert.Equal(new SteamVrBase("30", 0.25f, -1.5f, 0.125f, 1), now.Base);
    }

    [Fact]
    public void Without_a_room_setup_the_base_is_none()
    {
        Write(Lighthousedb, null);

        var now = SteamVrNow.Read(steam.Path, Stations);

        Assert.Equal(SteamVrBase.None, now.Base);
        Assert.Equal(new UniversePick("1500000001", false), now.Pick);
    }

    [Fact]
    public void Without_steam_there_is_no_base()
    {
        var now = SteamVrNow.Read(null, Stations);

        Assert.Null(now.Base);
        Assert.Equal(new UniversePick(null, false), now.Pick);
    }

    [Theory]
    [InlineData("not json", Vrchap)]
    [InlineData(Lighthousedb, "{ \"universes\" : [")]
    [InlineData("[]", Vrchap)]
    [InlineData("", Vrchap)]
    public void Files_that_cant_be_read_are_refused(string lighthousedb, string vrchap)
    {
        Write(lighthousedb, vrchap);

        var e = Assert.Throws<MiradorException>(() => SteamVrNow.Read(steam.Path, Stations));
        Assert.Equal("Couldn't read SteamVR's Lighthouse files; try again.", e.Message);
    }

    [Fact]
    public void Bases_compare_by_universe_and_standing()
    {
        var base30 = new SteamVrBase("30", 0.25f, -1.5f, 0.125f, 1);

        Assert.True(base30.SameAs(base30 with { Y = -1.5f + 1e-6f }));
        Assert.False(base30.SameAs(base30 with { Y = -1.4f }));
        Assert.False(base30.SameAs(base30 with { Yaw = 1.2f }));
        Assert.False(base30.SameAs(base30 with { Universe = "2" }));
        Assert.False(base30.SameAs(SteamVrBase.None));
        Assert.True(SteamVrBase.None.SameAs(new SteamVrBase(null, 0, 0, 0, 0)));
    }
}
