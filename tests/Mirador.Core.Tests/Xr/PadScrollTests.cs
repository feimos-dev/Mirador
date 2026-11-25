using Mirador.Core.Xr;

namespace Mirador.Core.Tests.Xr;

public class PadScrollTests
{
    [Fact]
    public void A_swipe_up_the_whole_pad_turns_the_wheel_up_by_the_pads_length()
    {
        var pad = new PadScroll();
        var turned = pad.Update(true, -1);
        for (var y = -0.9f; y <= 1.001f; y += 0.1f)
        {
            turned += pad.Update(true, y);
        }

        Assert.InRange(turned, PadScroll.WheelPerPad - PadScroll.MinStep, PadScroll.WheelPerPad);
    }

    [Fact]
    public void A_swipe_down_turns_it_down()
    {
        var pad = new PadScroll();
        pad.Update(true, 0.5f);

        Assert.True(pad.Update(true, 0) < 0);
    }

    [Fact]
    public void Small_movements_wait_until_they_make_a_step()
    {
        var pad = new PadScroll();
        pad.Update(true, 0);

        Assert.Equal(0, pad.Update(true, 0.05f));
        Assert.InRange(pad.Update(true, 0.1f), 35, 36);
    }

    [Fact]
    public void A_finger_put_down_or_lifted_turns_nothing()
    {
        var pad = new PadScroll();

        Assert.Equal(0, pad.Update(true, 0.8f));
        pad.Update(true, 0.85f);
        Assert.Equal(0, pad.Update(false, 0));
        Assert.Equal(0, pad.Update(true, -0.8f));
    }
}
