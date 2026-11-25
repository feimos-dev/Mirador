using Mirador.Core.Xr;

namespace Mirador.Core.Tests.Xr;

public class LaserButtonTests
{
    [Fact]
    public void The_button_goes_down_past_the_press_point_and_up_below_the_release_point()
    {
        var button = new LaserButton();

        Assert.False(button.Update(0.5f, onPanel: true));
        Assert.True(button.Update(0.8f, onPanel: true));
        Assert.True(button.Down);

        // Between the two points it stays as it is.
        Assert.False(button.Update(0.5f, onPanel: true));
        Assert.True(button.Down);

        Assert.True(button.Update(0.2f, onPanel: true));
        Assert.False(button.Down);
    }

    [Fact]
    public void A_held_trigger_keeps_the_button_down_off_the_panel_until_it_is_let_go()
    {
        var button = new LaserButton();
        button.Update(1, onPanel: true);

        Assert.False(button.Update(1, onPanel: false));
        Assert.True(button.Down);
        Assert.True(button.Update(0, onPanel: false));
        Assert.False(button.Down);
    }

    [Fact]
    public void A_trigger_pulled_off_the_panel_presses_nothing_even_once_the_laser_comes_on()
    {
        var button = new LaserButton();

        Assert.False(button.Update(1, onPanel: false));
        Assert.False(button.Update(1, onPanel: true));
        Assert.False(button.Down);

        // Let go and pulled again on the panel: that presses.
        button.Update(0, onPanel: true);
        Assert.True(button.Update(1, onPanel: true));
    }
}
