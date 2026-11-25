using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Playspace;

public static class RoomSetupMath
{
    public const float ControllerRestHeightM = 0.05f;

    public static MonadoPose Correct(MonadoPose currentOffset, float floorY, float headYaw)
    {
        var half = -headYaw / 2;
        var correction = new MonadoPose(0, MathF.Sin(half), 0, MathF.Cos(half), 0, -floorY, 0);
        return Transform(correction, currentOffset);
    }

    public static float YawOf(float qx, float qy, float qz, float qw)
    {
        var (fx, _, fz) = Rotate(new MonadoPose(qx, qy, qz, qw, 0, 0, 0), 0, 0, -1);
        return MathF.Atan2(-fx, -fz);
    }

    public static bool LooksAhead(MonadoPose head)
    {
        var (fx, _, fz) = Rotate(head, 0, 0, -1);
        return (fx * fx) + (fz * fz) >= 0.25f;
    }

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

    private static (float X, float Y, float Z) Rotate(MonadoPose q, float x, float y, float z)
    {
        var tx = 2 * ((q.Qy * z) - (q.Qz * y));
        var ty = 2 * ((q.Qz * x) - (q.Qx * z));
        var tz = 2 * ((q.Qx * y) - (q.Qy * x));
        return (
            x + (q.Qw * tx) + ((q.Qy * tz) - (q.Qz * ty)),
            y + (q.Qw * ty) + ((q.Qz * tx) - (q.Qx * tz)),
            z + (q.Qw * tz) + ((q.Qx * ty) - (q.Qy * tx)));
    }
}
