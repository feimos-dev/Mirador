using System.Numerics;
using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Xr;

/// <summary>
/// Where the dashboard goes and where a recentre puts the origin. Poses are OpenXR's: metres, +Y up, −Z forward.
/// </summary>
public static class DashboardMaths
{
    /// <summary>How far ahead of the head the dashboard opens.</summary>
    public const float AheadM = 1.5f;

    /// <summary>How far below the head the dashboard's bar is: low enough for the desktop panel above it to sit at eye height.</summary>
    public const float BelowM = 0.35f;

    /// <summary>How near and how far, along the floor, the dashboard can be moved from the head.</summary>
    public const float NearestM = 0.5f;
    public const float FarthestM = 2.5f;

    /// <summary>
    /// A pose <paramref name="ahead"/> metres in front of the head along the floor and <paramref name="below"/> metres
    /// under it, upright, with its −Z axis pointing back at the head. Without a way the head faces (it looks straight up
    /// or down), ahead is the stage's −Z.
    /// </summary>
    public static MonadoPose AheadOfHead(MonadoPose head, float ahead = AheadM, float below = BelowM)
    {
        var forward = Vector3.Transform(-Vector3.UnitZ, Rotation(head));
        forward.Y = 0;
        forward = forward.Length() > 1e-3f ? Vector3.Normalize(forward) : -Vector3.UnitZ;

        // Turned about the vertical to look back along forward: a turn of θ takes −Z to (−sin θ, 0, −cos θ).
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(forward.X, forward.Z));
        var at = new Vector3(head.X, head.Y, head.Z) + (forward * ahead) - new Vector3(0, below, 0);
        return Pose(turn, at);
    }

    /// <summary>
    /// The origin a recentre moves the stage to, in the stage as it is now: on the floor under the head, turned about
    /// the vertical to the way the head faces. Looking straight up or down, the way the head's top or bottom points.
    /// </summary>
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

    /// <summary>
    /// <paramref name="at"/>, moved along the floor towards or away from <paramref name="head"/> to between
    /// <see cref="NearestM"/> and <see cref="FarthestM"/> of it; its height stays. Right over the head, it goes nowhere.
    /// </summary>
    public static Vector3 WithinReach(Vector3 head, Vector3 at)
    {
        var along = new Vector2(at.X - head.X, at.Z - head.Z);
        var distance = along.Length();
        if (distance < 1e-4f)
        {
            return at;
        }

        var reach = Math.Clamp(distance, NearestM, FarthestM);
        along *= reach / distance;
        return new Vector3(head.X + along.X, at.Y, head.Z + along.Y);
    }

    /// <summary>Where the head is in the stage once a recentre from <paramref name="headInStage"/> has moved it.</summary>
    public static MonadoPose HeadAfterRecentre(MonadoPose headInStage) => Compose(Inverse(RecentredOrigin(headInStage)), headInStage);

    /// <summary><paramref name="child"/>, given in <paramref name="parent"/>'s space, in the space <paramref name="parent"/> is given in.</summary>
    public static MonadoPose Compose(MonadoPose parent, MonadoPose child)
    {
        var rotation = Rotation(parent);
        var at = Vector3.Transform(new Vector3(child.X, child.Y, child.Z), rotation) + new Vector3(parent.X, parent.Y, parent.Z);
        return Pose(rotation * Rotation(child), at);
    }

    /// <summary>The pose that undoes <paramref name="pose"/>.</summary>
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
