using Mirador.Core.Xr;

namespace Mirador.Core.Tests.Xr;

public class SystemButtonTests
{
    [Fact]
    public void A_press_toggles_when_the_button_is_let_go()
    {
        var button = new SystemButton();

        Assert.Equal(SystemButtonAction.None, button.Update(true, 10.0));
        Assert.Equal(SystemButtonAction.None, button.Update(true, 10.3));
        Assert.Equal(SystemButtonAction.Toggle, button.Update(false, 10.4));
        Assert.Equal(SystemButtonAction.None, button.Update(false, 10.5));
    }

    [Fact]
    public void A_hold_recentres_once_and_does_not_toggle_when_let_go()
    {
        var button = new SystemButton();

        Assert.Equal(SystemButtonAction.None, button.Update(true, 5.0));
        Assert.Equal(SystemButtonAction.None, button.Update(true, 5.9));
        Assert.Equal(SystemButtonAction.Recentre, button.Update(true, 6.0));
        Assert.Equal(SystemButtonAction.None, button.Update(true, 7.5));
        Assert.Equal(SystemButtonAction.None, button.Update(false, 7.6));
    }

    [Fact]
    public void A_button_let_go_late_with_no_frame_in_between_does_nothing()
    {
        var button = new SystemButton();

        Assert.Equal(SystemButtonAction.None, button.Update(true, 1.0));
        Assert.Equal(SystemButtonAction.None, button.Update(false, 3.0));
    }

    [Fact]
    public void Each_press_is_judged_on_its_own()
    {
        var button = new SystemButton();
        button.Update(true, 0.0);
        button.Update(true, 1.0);
        button.Update(false, 1.1);

        Assert.Equal(SystemButtonAction.None, button.Update(true, 2.0));
        Assert.Equal(SystemButtonAction.Toggle, button.Update(false, 2.2));
    }
}
