using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Playspace;

/// <summary>
/// The room setup's arithmetic. Poses are in OpenXR's stage space: right-handed, +Y up, forward is −Z, metres.
/// Monado places a device of a tracking origin as <c>pose_in_stage = offset * pose_in_origin</c>: the device's pose in its
/// origin, then the origin's offset (the offset's rotation, then its position). A correction measured in the stage
/// space therefore goes in front of the offset already set: <c>new_offset = correction * current_offset</c>.
/// </summary>
public static class RoomSetupMath
{
    /// <summary>How high a controller's grip is above the floor when the controller lies on it.</summary>
    public const float ControllerRestHeightM = 0.05f;

    /// <summary>
    /// The origin offset that puts the measured floor at y = 0 and turns the head's forward to −Z: the current offset,
    /// lifted by −<paramref name="floorY"/> and turned about the stage's vertical axis by −<paramref name="headYaw"/>.
    /// </summary>
    /// <param name="currentOffset">The origin's offset while the measurement was taken.</param>
    /// <param name="floorY">The floor's height in the stage space, as measured (0 leaves the height).</param>
    /// <param name="headYaw">The head's <see cref="YawOf"/>, as measured (0 leaves the direction).</param>
    public static MonadoPose Correct(MonadoPose currentOffset, float floorY, float headYaw)
    {
        var half = -headYaw / 2;
        var correction = new MonadoPose(0, MathF.Sin(half), 0, MathF.Cos(half), 0, -floorY, 0);
        return Transform(correction, currentOffset);
    }

    /// <summary>
    /// The angle about +Y (counter-clockwise seen from above, radians, −π to π) by which the rotation turns −Z: where it
    /// faces, whatever it pitches or rolls. 0 faces −Z, −π/2 faces +X.
    /// </summary>
    public static float YawOf(float qx, float qy, float qz, float qw)
    {
        // The rotated −Z, flattened onto the floor. A rotation of θ about +Y turns −Z to (−sin θ, 0, −cos θ).
        var (fx, _, fz) = Rotate(new MonadoPose(qx, qy, qz, qw, 0, 0, 0), 0, 0, -1);
        return MathF.Atan2(-fx, -fz);
    }

    /// <summary>
    /// Whether <paramref name="head"/> looks ahead enough for <see cref="YawOf"/> to tell forward: at most about 60° up or
    /// down, so that at least half of its view direction lies along the floor.
    /// </summary>
    public static bool LooksAhead(MonadoPose head)
    {
        var (fx, _, fz) = Rotate(head, 0, 0, -1);
        return (fx * fx) + (fz * fz) >= 0.25f;
    }

    /// <summary>
    /// <paramref name="pose"/>, given relative to <paramref name="offset"/>, in the space <paramref name="offset"/> is
    /// in: <c>offset * pose</c>. The rotation is normalised.
    /// </summary>
    public static MonadoPose Transform(MonadoPose offset, MonadoPose pose)
    {
        var (x, y, z) = Rotate(offset, pose.X, pose.Y, pose.Z);
        var qx = (offset.Qw * pose.Qx) + (offset.Qx * pose.Qw) + (offset.Qy * pose.Qz) - (offset.Qz * pose.Qy);
        var qy = (offset.Qw * pose.Qy) - (offset.Qx * pose.Qz) + (offset.Qy * pose.Qw) + (offset.Qz * pose.Qx);
        var qz = (offset.Qw * pose.Qz) + (offset.Qx * pose.Qy) - (offset.Qy * pose.Qx) + (offset.Qz * pose.Qw);
        var qw = (offset.Qw * pose.Qw) - (offset.Qx * pose.Qx) - (offset.Qy * pose.Qy) - (offset.Qz * pose.Qz);
        var length = MathF.Sqrt((qx * qx) + (qy * qy) + (qz * qz) + (qw * qw));
        return new MonadoPose(qx / length, qy / length, qz / length, qw / length, x + offset.X, y + offset.Y, z + offset.Z);
    }

    /// <summary>
    /// The floor's height from the controllers' grips while one lies on the floor: the lower grip, less
    /// <see cref="ControllerRestHeightM"/>; null when neither was tracked.
    /// </summary>
    public static float? FloorY(MonadoPose? left, MonadoPose? right)
    {
        float? lowest = (left, right) switch
        {
            ({ } l, { } r) => MathF.Min(l.Y, r.Y),
            ({ } l, null) => l.Y,
            (null, { } r) => r.Y,
            _ => null,
        };
        return lowest - ControllerRestHeightM;
    }

    /// <summary>The vector (x, y, z) turned by the pose's rotation (a unit quaternion).</summary>
    private static (float X, float Y, float Z) Rotate(MonadoPose q, float x, float y, float z)
    {
        // v' = v + w t + q × t, with t = 2 (q × v).
        var tx = 2 * ((q.Qy * z) - (q.Qz * y));
        var ty = 2 * ((q.Qz * x) - (q.Qx * z));
        var tz = 2 * ((q.Qx * y) - (q.Qy * x));
        return (
            x + (q.Qw * tx) + ((q.Qy * tz) - (q.Qz * ty)),
            y + (q.Qw * ty) + ((q.Qz * tx) - (q.Qx * tz)),
            z + (q.Qw * tz) + ((q.Qx * ty) - (q.Qy * tx)));
    }
}
