using Mirador.Core.Xr;

namespace Mirador.Core.Tests.Xr;

public class LaserClickTests
{
    private const float W = 1.4f;
    private const float H = 0.7875f;

    [Fact]
    public void At_rest_the_pointer_follows_the_laser()
    {
        var click = new LaserClick();

        Assert.Equal((0.3f, 0.4f), click.Pointer((0.3f, 0.4f), 0, false, W, H));
        Assert.Equal((0.6f, 0.2f), click.Pointer((0.6f, 0.2f), 0.05f, false, W, H));
    }

    [Fact]
    public void A_click_lands_where_the_laser_was_before_the_trigger_moved()
    {
        var click = new LaserClick();
        click.Pointer((0.5f, 0.5f), 0, false, W, H);

        Assert.Equal((0.5f, 0.5f), click.Pointer((0.5f, 0.51f), 0.4f, false, W, H));
        Assert.Equal((0.5f, 0.5f), click.Pointer((0.505f, 0.525f), 0.9f, true, W, H));
        Assert.Equal((0.5f, 0.5f), click.Pointer((0.505f, 0.52f), 0.3f, false, W, H));

        Assert.Equal((0.505f, 0.52f), click.Pointer((0.505f, 0.52f), 0, false, W, H));
    }

    [Fact]
    public void A_press_moved_further_than_the_hold_is_a_drag_that_follows_the_laser()
    {
        var click = new LaserClick();
        click.Pointer((0.5f, 0.5f), 0, false, W, H);
        click.Pointer((0.5f, 0.5f), 1, true, W, H);

        Assert.Equal((0.54f, 0.5f), click.Pointer((0.54f, 0.5f), 1, true, W, H));
        Assert.Equal((0.51f, 0.5f), click.Pointer((0.51f, 0.5f), 1, true, W, H));
    }

    [Fact]
    public void A_laser_that_comes_onto_the_panel_with_the_trigger_pulled_holds_where_it_came_on()
    {
        var click = new LaserClick();
        click.Pointer((0.1f, 0.1f), 0, false, W, H);
        click.Left();

        Assert.Equal((0.7f, 0.7f), click.Pointer((0.7f, 0.7f), 0.5f, false, W, H));
        Assert.Equal((0.7f, 0.7f), click.Pointer((0.71f, 0.7f), 1, true, W, H));
    }
}
