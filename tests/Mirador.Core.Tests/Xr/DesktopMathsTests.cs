using System.Numerics;
using Mirador.Core.Xr;

namespace Mirador.Core.Tests.Xr;

public class DesktopMathsTests
{
    // A panel 2 m wide and 1 m high, 1.5 m ahead at eye height, facing the viewer at the origin (+Z).
    private static readonly Vector3 Centre = new(0, 1.5f, -1.5f);

    private static (float U, float V)? Point(Vector3 start, Vector3 end) =>
        DesktopMaths.PanelPoint(start, end, Centre, Vector3.UnitX, Vector3.UnitY, 2, 1);

    [Fact]
    public void A_ray_at_the_panels_middle_is_the_middle()
    {
        var point = Point(new Vector3(0, 1.5f, 0), new Vector3(0, 1.5f, -100));

        Assert.NotNull(point);
        Assert.Equal(0.5f, point.Value.U, 4);
        Assert.Equal(0.5f, point.Value.V, 4);
    }

    [Fact]
    public void Up_and_to_the_right_is_towards_the_top_right_corner()
    {
        // Hits (0.5, 1.75, -1.5): a quarter of the width right of the middle, a quarter of the height above it.
        var point = Point(new Vector3(0.5f, 1.75f, 0), new Vector3(0.5f, 1.75f, -100));

        Assert.NotNull(point);
        Assert.Equal(0.75f, point.Value.U, 4);
        Assert.Equal(0.25f, point.Value.V, 4);
    }

    [Fact]
    public void A_ray_past_the_edge_away_from_the_panel_along_it_or_too_short_misses()
    {
        Assert.Null(Point(new Vector3(1.2f, 1.5f, 0), new Vector3(1.2f, 1.5f, -100)));
        Assert.Null(Point(new Vector3(0, 1.5f, 0), new Vector3(0, 1.5f, 100)));
        Assert.Null(Point(new Vector3(0, 1.5f, 0), new Vector3(100, 1.5f, 0)));
        Assert.Null(Point(new Vector3(0, 1.5f, 0), new Vector3(0, 1.5f, -1)));
    }

    [Fact]
    public void A_turned_panel_is_measured_along_its_own_edges()
    {
        // A panel to the viewer's left, facing +X: its right edge runs along -Z.
        var point = DesktopMaths.PanelPoint(
            new Vector3(0, 1, -0.25f), new Vector3(-100, 1, -0.25f), new Vector3(-2, 1, 0), -Vector3.UnitZ, Vector3.UnitY, 1, 1);

        Assert.NotNull(point);
        Assert.Equal(0.75f, point.Value.U, 4);
        Assert.Equal(0.5f, point.Value.V, 4);
    }

    [Fact]
    public void A_monitors_corners_map_onto_the_virtual_desktop()
    {
        // Two 1920x1080 monitors side by side, the second one the panel's; the virtual desktop spans both.
        var monitor = new DesktopRect(1920, 0, 1920, 1080);
        var all = new DesktopRect(0, 0, 3840, 1080);

        Assert.Equal((32776, 0), DesktopMaths.Absolute(0, 0, monitor, all));
        Assert.Equal((65535, 65535), DesktopMaths.Absolute(1, 1, monitor, all));
    }

    [Fact]
    public void A_monitor_left_of_the_primary_one_has_negative_pixels_and_still_maps_from_zero()
    {
        var monitor = new DesktopRect(-1920, 0, 1920, 1080);
        var all = new DesktopRect(-1920, 0, 3840, 1080);

        Assert.Equal((0, 0), DesktopMaths.Absolute(0, 0, monitor, all));
        Assert.Equal((32759, 65535), DesktopMaths.Absolute(1, 1, monitor, all));
    }

    [Fact]
    public void A_point_outside_the_panel_stays_on_the_monitor()
    {
        var monitor = new DesktopRect(0, 0, 1920, 1080);

        Assert.Equal((65535, 0), DesktopMaths.Absolute(1.4f, -0.2f, monitor, monitor));
    }

    [Fact]
    public void A_wide_frame_is_read_back_at_half_size()
    {
        Assert.Equal((1920, 1080, false), DesktopMaths.Readback(1920, 1080));
        Assert.Equal((2560, 1440, false), DesktopMaths.Readback(2560, 1440));
        Assert.Equal((1920, 1080, true), DesktopMaths.Readback(3840, 2160));
    }
}
