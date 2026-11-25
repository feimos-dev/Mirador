using Mirador.Core.Xr;

namespace Mirador.Core.Tests.Xr;

public class RayGrabTests
{
    [Fact]
    public void The_distance_follows_the_hands_reach()
    {
        var grab = new RayGrab();
        grab.Start(1.5f, 0.3f);

        Assert.Equal(1.5f, grab.Update(0.3f), 4);
        Assert.Equal(3f, grab.Update(0.6f), 4);
        Assert.Equal(0.75f, grab.Update(0.15f), 4);
    }

    [Fact]
    public void Pushed_past_the_farthest_it_stays_and_pulling_back_brings_it_closer_at_once()
    {
        var grab = new RayGrab();
        grab.Start(2, 0.3f);

        Assert.Equal(RayGrab.FarthestM, grab.Update(0.9f), 4); // 6 m asked for
        Assert.Equal(RayGrab.FarthestM, grab.Update(1.2f), 4); // still pushing
        Assert.True(grab.Update(1.1f) < RayGrab.FarthestM);     // the first pull back already comes closer
    }

    [Fact]
    public void A_hand_at_the_head_keeps_the_distance_finite()
    {
        var grab = new RayGrab();
        grab.Start(1, 0);

        Assert.True(float.IsFinite(grab.Update(0)));
        Assert.True(grab.Update(0) > 0);
    }
}
