using System.Numerics;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Xr;

namespace Mirador.Core.Tests.Xr;

public class DashboardMathsTests
{
    private static MonadoPose Head(float yawDegrees, float pitchDegrees, float x, float y, float z)
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDegrees * MathF.PI / 180)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitchDegrees * MathF.PI / 180);
        return new MonadoPose(q.X, q.Y, q.Z, q.W, x, y, z);
    }

    private static Vector3 Rotate(MonadoPose pose, Vector3 v) => Vector3.Transform(v, new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw));

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True((expected - actual).Length() < 1e-4f, $"expected {expected}, got {actual}");

    [Fact]
    public void The_dashboard_opens_ahead_of_a_head_looking_along_minus_z_and_faces_it()
    {
        var panel = DashboardMaths.AheadOfHead(Head(0, 0, 0.5f, 1.7f, 0.2f));

        Near(new Vector3(0.5f, 1.35f, -1.3f), new Vector3(panel.X, panel.Y, panel.Z));
        Near(Vector3.UnitZ, Rotate(panel, -Vector3.UnitZ));
    }

    [Fact]
    public void A_head_turned_left_and_looking_down_gets_an_upright_dashboard_to_its_left()
    {
        var panel = DashboardMaths.AheadOfHead(Head(90, -30, 0, 1.6f, 0));

        Near(new Vector3(-1.5f, 1.25f, 0), new Vector3(panel.X, panel.Y, panel.Z));
        Near(Vector3.UnitX, Rotate(panel, -Vector3.UnitZ));
        Near(Vector3.UnitY, Rotate(panel, Vector3.UnitY));
    }

    [Fact]
    public void A_head_looking_straight_down_gets_the_dashboard_along_the_stages_minus_z()
    {
        var panel = DashboardMaths.AheadOfHead(Head(0, -90, 0, 1.6f, 0));

        Near(new Vector3(0, 1.25f, -1.5f), new Vector3(panel.X, panel.Y, panel.Z));
    }

    [Fact]
    public void A_recentre_puts_the_origin_on_the_floor_under_the_head_turned_its_way()
    {
        var origin = DashboardMaths.RecentredOrigin(Head(90, 20, 1, 1.7f, -2));

        Near(new Vector3(1, 0, -2), new Vector3(origin.X, origin.Y, origin.Z));
        Near(-Vector3.UnitX, Rotate(origin, -Vector3.UnitZ));
        Near(Vector3.UnitY, Rotate(origin, Vector3.UnitY));
    }

    [Fact]
    public void Looking_straight_down_the_recentre_turns_the_way_the_heads_top_points()
    {
        var origin = DashboardMaths.RecentredOrigin(Head(90, -90, 0, 1.7f, 0));

        Near(-Vector3.UnitX, Rotate(origin, -Vector3.UnitZ));
    }

    [Fact]
    public void After_a_recentre_the_head_is_over_the_new_origin_facing_minus_z()
    {
        var head = Head(135, 10, 0.4f, 1.7f, 0.9f);

        var after = DashboardMaths.HeadAfterRecentre(head);

        Near(new Vector3(0, 1.7f, 0), new Vector3(after.X, after.Y, after.Z));
        var forward = Rotate(after, -Vector3.UnitZ);
        Assert.True(MathF.Abs(forward.X) < 1e-4f && forward.Z < 0, $"forward {forward}");
    }

    [Fact]
    public void A_recentre_builds_on_the_offset_already_there()
    {
        var now = new MonadoPose(0, 0, 0, 1, 2, 0.1f, 3);
        var origin = DashboardMaths.RecentredOrigin(Head(0, 0, 1, 1.7f, 1));

        var target = DashboardMaths.Compose(now, origin);

        Near(new Vector3(3, 0.1f, 4), new Vector3(target.X, target.Y, target.Z));
    }

    [Fact]
    public void Inverse_undoes_a_pose()
    {
        var pose = Head(40, 15, 1, 2, 3);

        var none = DashboardMaths.Compose(pose, DashboardMaths.Inverse(pose));

        Near(Vector3.Zero, new Vector3(none.X, none.Y, none.Z));
        Assert.True(MathF.Abs(MathF.Abs(none.Qw) - 1) < 1e-4f);
    }
}
