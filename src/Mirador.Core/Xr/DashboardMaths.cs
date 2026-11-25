using System.Numerics;
using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Xr;

public static class DashboardMaths
{
    public const float AheadM = 1.5f;

    public const float BelowM = 0.35f;

    public static MonadoPose AheadOfHead(MonadoPose head, float ahead = AheadM, float below = BelowM)
    {
        var forward = Vector3.Transform(-Vector3.UnitZ, Rotation(head));
        forward.Y = 0;
        forward = forward.Length() > 1e-3f ? Vector3.Normalize(forward) : -Vector3.UnitZ;

        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(forward.X, forward.Z));
        var at = new Vector3(head.X, head.Y, head.Z) + (forward * ahead) - new Vector3(0, below, 0);
        return Pose(turn, at);
    }

    public static MonadoPose RecentredOrigin(MonadoPose headInStage)
    {
        var rotation = Rotation(headInStage);
        var forward = Vector3.Transform(-Vector3.UnitZ, rotation);
        var down = forward.Y < 0;
        forward.Y = 0;
        if (forward.Length() < 1e-3f)
        {
            forward = Vector3.Transform(Vector3.UnitY, rotation) * (down ? 1f : -1f);
            forward.Y = 0;
        }

        var yaw = forward.Length() < 1e-3f ? 0f : MathF.Atan2(-forward.X, -forward.Z);
        return Pose(Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw), new Vector3(headInStage.X, 0, headInStage.Z));
    }

    public static MonadoPose HeadAfterRecentre(MonadoPose headInStage) => Compose(Inverse(RecentredOrigin(headInStage)), headInStage);

    public static MonadoPose Compose(MonadoPose parent, MonadoPose child)
    {
        var rotation = Rotation(parent);
        var at = Vector3.Transform(new Vector3(child.X, child.Y, child.Z), rotation) + new Vector3(parent.X, parent.Y, parent.Z);
        return Pose(rotation * Rotation(child), at);
    }

    public static MonadoPose Inverse(MonadoPose pose)
    {
        var rotation = Quaternion.Conjugate(Rotation(pose));
        return Pose(rotation, Vector3.Transform(-new Vector3(pose.X, pose.Y, pose.Z), rotation));
    }

    private static Quaternion Rotation(MonadoPose pose)
    {
        var q = new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
        return q.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(q);
    }

    private static MonadoPose Pose(Quaternion rotation, Vector3 at)
    {
        rotation = Quaternion.Normalize(rotation);
        return new MonadoPose(rotation.X, rotation.Y, rotation.Z, rotation.W, at.X, at.Y, at.Z);
    }
}
