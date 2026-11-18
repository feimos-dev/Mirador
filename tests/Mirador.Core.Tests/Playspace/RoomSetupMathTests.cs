using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Playspace;

namespace Mirador.Core.Tests.Playspace;

/// <summary>
/// The stage space is OpenXR's: right-handed, +Y up, forward is −Z. Monado places a device as
/// <c>pose_in_stage = offset * pose_in_origin</c>, the origin's offset applied after the device's own pose.
/// </summary>
public class RoomSetupMathTests
{
    private const float Tolerance = 1e-4f;
    private static readonly float S = MathF.Sqrt(0.5f);

    /// <summary>A rotation of <paramref name="radians"/> about +Y (counter-clockwise seen from above).</summary>
    private static MonadoPose Yaw(float radians, float x = 0, float y = 0, float z = 0) =>
        new(0, MathF.Sin(radians / 2), 0, MathF.Cos(radians / 2), x, y, z);

    /// <summary>A pose whose quaternion is scaled to length 1.</summary>
    private static MonadoPose Unit(float qx, float qy, float qz, float qw, float x, float y, float z)
    {
        var n = MathF.Sqrt((qx * qx) + (qy * qy) + (qz * qz) + (qw * qw));
        return new MonadoPose(qx / n, qy / n, qz / n, qw / n, x, y, z);
    }

    /// <summary>Equal as rotations (q and −q are the same one) and as positions.</summary>
    private static void Same(MonadoPose expected, MonadoPose actual)
    {
        var sign = (expected.Qx * actual.Qx) + (expected.Qy * actual.Qy) + (expected.Qz * actual.Qz) + (expected.Qw * actual.Qw) < 0 ? -1 : 1;
        Assert.Equal(expected.Qx, sign * actual.Qx, Tolerance);
        Assert.Equal(expected.Qy, sign * actual.Qy, Tolerance);
        Assert.Equal(expected.Qz, sign * actual.Qz, Tolerance);
        Assert.Equal(expected.Qw, sign * actual.Qw, Tolerance);
        Assert.Equal(expected.X, actual.X, Tolerance);
        Assert.Equal(expected.Y, actual.Y, Tolerance);
        Assert.Equal(expected.Z, actual.Z, Tolerance);
    }

    [Fact]
    public void A_floor_measured_at_minus_ten_cm_lifts_by_ten_cm()
    {
        Same(MonadoPose.Identity with { Y = 0.1f }, RoomSetupMath.Correct(MonadoPose.Identity, -0.1f, 0));

        // On top of the offset there is already: lifted by ten more.
        var current = Yaw(0.5f, 1, 0.3f, -2);
        Same(current with { Y = 0.4f }, RoomSetupMath.Correct(current, -0.1f, 0));

        // A floor measured too high lowers the origin.
        Same(MonadoPose.Identity with { Y = -0.25f }, RoomSetupMath.Correct(MonadoPose.Identity, 0.25f, 0));
    }

    [Fact]
    public void Facing_plus_x_turns_ninety_degrees()
    {
        // Facing +X is a quarter turn clockwise seen from above: −90° about +Y.
        var head = Yaw(-MathF.PI / 2, 0.5f, 1.7f, 0);
        Assert.Equal(-MathF.PI / 2, RoomSetupMath.YawOf(head.Qx, head.Qy, head.Qz, head.Qw), Tolerance);

        var corrected = RoomSetupMath.Correct(MonadoPose.Identity, 0, RoomSetupMath.YawOf(head.Qx, head.Qy, head.Qz, head.Qw));
        Same(new MonadoPose(0, S, 0, S, 0, 0, 0), corrected);

        // The head now faces −Z; the turn is about the stage's vertical axis, so its place turns too (+X goes to −Z).
        var moved = RoomSetupMath.Transform(corrected, head);
        Assert.Equal(0, RoomSetupMath.YawOf(moved.Qx, moved.Qy, moved.Qz, moved.Qw), Tolerance);
        Same(new MonadoPose(0, 0, 0, 1, 0, 1.7f, -0.5f), moved);
    }

    [Fact]
    public void Correcting_twice_with_new_measurements_converges()
    {
        var current = Yaw(2.1f, -0.4f, 0.2f, 1.3f);
        var once = RoomSetupMath.Correct(current, -0.07f, 0.8f);

        // Measured again after the correction there is no error left: correcting with it changes nothing.
        Same(once, RoomSetupMath.Correct(once, 0, 0));
    }

    [Fact]
    public void A_measurement_through_the_offset_comes_out_level_and_forward()
    {
        // Devices in their tracking origin, seen through an offset already set.
        var current = Yaw(0.3f, 0.2f, -0.05f, 0.6f);
        var controllerInOrigin = Yaw(1.1f, 0.4f, 0.12f, -0.9f);
        var headInOrigin = Unit(0.1f, 0.6f, 0.05f, 0.79f, -0.3f, 1.6f, 0.2f);

        // The floor: the resting controller's grip, less the height it rests at.
        var controller = RoomSetupMath.Transform(current, controllerInOrigin);
        var floorY = controller.Y - RoomSetupMath.ControllerRestHeightM;
        var lifted = RoomSetupMath.Correct(current, floorY, 0);
        Assert.Equal(RoomSetupMath.ControllerRestHeightM, RoomSetupMath.Transform(lifted, controllerInOrigin).Y, Tolerance);

        // Forward: the head's yaw.
        var head = RoomSetupMath.Transform(lifted, headInOrigin);
        var turned = RoomSetupMath.Correct(lifted, 0, RoomSetupMath.YawOf(head.Qx, head.Qy, head.Qz, head.Qw));
        var headAfter = RoomSetupMath.Transform(turned, headInOrigin);
        Assert.Equal(0, RoomSetupMath.YawOf(headAfter.Qx, headAfter.Qy, headAfter.Qz, headAfter.Qw), Tolerance);

        // Turning keeps the floor where it was.
        Assert.Equal(RoomSetupMath.ControllerRestHeightM, RoomSetupMath.Transform(turned, controllerInOrigin).Y, Tolerance);
    }

    [Fact]
    public void Looking_ahead_is_up_to_sixty_degrees_up_or_down()
    {
        static MonadoPose Pitch(float radians) => new(MathF.Sin(radians / 2), 0, 0, MathF.Cos(radians / 2), 0, 0, 0);

        Assert.True(RoomSetupMath.LooksAhead(MonadoPose.Identity));
        Assert.True(RoomSetupMath.LooksAhead(Pitch(1.0f)));
        Assert.True(RoomSetupMath.LooksAhead(Pitch(-1.0f)));
        Assert.False(RoomSetupMath.LooksAhead(Pitch(1.1f)));
        Assert.False(RoomSetupMath.LooksAhead(Pitch(-MathF.PI / 2)));
    }

    [Fact]
    public void Yaw_of_identity_is_zero()
    {
        Assert.Equal(0, RoomSetupMath.YawOf(0, 0, 0, 1), Tolerance);
    }

    [Fact]
    public void Yaw_ignores_looking_up_or_down_and_tilting()
    {
        // Facing +X, then pitched 30° down and rolled 10°: still −90°.
        var facing = Yaw(-MathF.PI / 2);
        var pitch = new MonadoPose(MathF.Sin(-0.2618f), 0, 0, MathF.Cos(-0.2618f), 0, 0, 0);
        var roll = new MonadoPose(0, 0, MathF.Sin(0.0873f), MathF.Cos(0.0873f), 0, 0, 0);
        var head = RoomSetupMath.Transform(RoomSetupMath.Transform(facing, pitch), roll);
        Assert.Equal(-MathF.PI / 2, RoomSetupMath.YawOf(head.Qx, head.Qy, head.Qz, head.Qw), 1e-3f);

        // Facing backwards (+Z) is half a turn.
        var back = Yaw(MathF.PI);
        Assert.Equal(MathF.PI, MathF.Abs(RoomSetupMath.YawOf(back.Qx, back.Qy, back.Qz, back.Qw)), Tolerance);
    }

    [Fact]
    public void The_floor_is_the_lower_controller_less_its_rest_height()
    {
        Assert.Equal(0.05f, RoomSetupMath.ControllerRestHeightM);
        var low = MonadoPose.Identity with { Y = -0.03f };
        var high = MonadoPose.Identity with { Y = 0.9f };

        Assert.Equal(-0.08f, RoomSetupMath.FloorY(low, high)!.Value, Tolerance);
        Assert.Equal(-0.08f, RoomSetupMath.FloorY(high, low)!.Value, Tolerance);
        Assert.Equal(0.85f, RoomSetupMath.FloorY(null, high)!.Value, Tolerance);
        Assert.Null(RoomSetupMath.FloorY(null, null));
    }

    [Fact]
    public void The_result_stays_a_unit_rotation()
    {
        var current = new MonadoPose(0.01f, 0.7f, -0.02f, 0.71f, 0, 0, 0);
        var corrected = current;
        for (var i = 0; i < 100; i++)
        {
            corrected = RoomSetupMath.Correct(corrected, 0.001f, 0.05f);
        }

        var length = (corrected.Qx * corrected.Qx) + (corrected.Qy * corrected.Qy) + (corrected.Qz * corrected.Qz) + (corrected.Qw * corrected.Qw);
        Assert.Equal(1, length, Tolerance);
    }
}
