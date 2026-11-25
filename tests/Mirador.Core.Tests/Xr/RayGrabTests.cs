using Mirador.Core.Xr;

namespace Mirador.Core.Tests.Xr;

public class RayGrabTests
{
    private const float Frame = 1 / 90f;

    [Fact]
    public void The_distance_follows_the_hands_reach()
    {
        var grab = new RayGrab();
        grab.Start(1.5f, 0.3f);

        Assert.Equal(1.5f, grab.Update(0.3f, 0, Frame), 4);
        Assert.Equal(3f, grab.Update(0.6f, 0, Frame), 4);
        Assert.Equal(0.75f, grab.Update(0.15f, 0, Frame), 4);
    }

    [Fact]
    public void The_thumbstick_pushes_away_and_pulls_in()
    {
        var grab = new RayGrab();
        grab.Start(1.5f, 0.3f);

        var pushed = 0f;
        for (var i = 0; i < 45; i++) // half a second, all the way forward
        {
            pushed = grab.Update(0.3f, 1, Frame);
        }

        Assert.InRange(pushed, 2.8f, 3.2f);
        Assert.True(grab.Update(0.3f, -1, Frame) < pushed);

        // A stick barely off centre pushes nothing.
        var still = grab.Update(0.3f, 0, Frame);
        Assert.Equal(still, grab.Update(0.3f, RayGrab.StickDeadZone / 2, Frame), 5);
    }

    [Fact]
    public void Pushed_past_the_farthest_it_stays_and_pulling_back_brings_it_closer_at_once()
    {
        var grab = new RayGrab();
        grab.Start(2, 0.3f);

        for (var i = 0; i < 300; i++) // the stick held forward far longer than 5 m needs
        {
            Assert.True(grab.Update(0.3f, 1, Frame) <= RayGrab.FarthestM);
        }

        Assert.Equal(RayGrab.FarthestM, grab.Update(0.3f, 1, Frame), 4);
        Assert.True(grab.Update(0.3f, -1, Frame) < RayGrab.FarthestM); // the first pull already comes closer

        // The same for the reach.
        Assert.Equal(RayGrab.FarthestM, grab.Update(0.9f, 0, Frame), 4);
        Assert.True(grab.Update(0.85f, 0, Frame) < RayGrab.FarthestM);
    }

    [Fact]
    public void Pulled_in_it_stops_short_of_the_hand_and_pushing_out_works_at_once()
    {
        var grab = new RayGrab();
        grab.Start(0.5f, 0.3f);

        for (var i = 0; i < 300; i++)
        {
            grab.Update(0.3f, -1, Frame);
        }

        Assert.Equal(RayGrab.NearestM, grab.Update(0.3f, -1, Frame), 4);
        Assert.True(grab.Update(0.3f, 1, Frame) > RayGrab.NearestM);
    }

    [Fact]
    public void A_hand_at_the_head_keeps_the_distance_finite()
    {
        var grab = new RayGrab();
        grab.Start(1, 0);

        Assert.True(float.IsFinite(grab.Update(0, 0, Frame)));
    }
}
