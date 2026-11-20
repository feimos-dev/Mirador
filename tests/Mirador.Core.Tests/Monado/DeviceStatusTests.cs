using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Tests.Monado;

public class DeviceStatusTests
{
    private static MonadoDevice Device(
        int index, string? role, string name, string? serial = null, bool battery = false, bool charging = false, float charge = 0) =>
        new(index, role, name, serial, 0, battery, charging, charge);

    [Fact]
    public void Lines_list_only_the_headset_and_eye_tracker()
    {
        var lines = DeviceStatus.Lines([
            Device(3, "right", "Index Controller R", battery: true, charge: 0.5f),
            Device(0, "head", "Pimax Dream Air"),
            Device(4, "hand-tracking-unobstructed-right", "Right hand tracker"),
            Device(1, "eyes", "Eye tracker"),
            Device(2, "left", "Index Controller L"),
            Device(5, "gamepad", "Xbox controller"),
            Device(6, null, "Tracker"),
        ]);

        Assert.Equal(
            [
                new StatusLine("device-0", "Headset", StatusLevel.Ok, "Pimax Dream Air"),
                new StatusLine("device-1", "Eye tracker", StatusLevel.Ok, "Eye tracker"),
            ],
            lines);
    }

    [Fact]
    public void A_device_in_two_roles_is_listed_once()
    {
        var lines = DeviceStatus.Lines([Device(0, "eyes", "Headset"), Device(0, "head", "Headset")]);

        Assert.Equal([new StatusLine("device-0", "Headset", StatusLevel.Ok, "Headset")], lines);
    }

    [Fact]
    public void No_devices_give_no_lines() => Assert.Empty(DeviceStatus.Lines([]));

    [Fact]
    public void The_serial_and_battery_follow_the_name()
    {
        var lines = DeviceStatus.Lines([
            Device(0, "head", "Pimax Dream Air", "XR5DA-P501"),
            Device(1, "eyes", "Eye tracker", "", battery: true, charging: true, charge: 0.874f),
        ]);

        Assert.Equal(["Pimax Dream Air, serial XR5DA-P501", "Eye tracker, 87% (charging)"], lines.Select(l => l.Text));
    }

    [Fact]
    public void Controllers_take_hand_and_battery_from_the_device_of_the_same_serial()
    {
        var controllers = DeviceStatus.Controllers(
            [new TrackedDevice(TrackedKind.Controller, "LHR-AAAA"), new TrackedDevice(TrackedKind.Controller, "LHR-BBBB")],
            [
                Device(0, "head", "Headset", "LHR-HEAD"),
                Device(2, "right", "Index Controller", "LHR-BBBB", battery: true, charge: 0.5f),
                Device(1, "left", "Index Controller", "lhr-aaaa", battery: true, charging: true, charge: 0.874f),
            ]);

        Assert.Equal(
            [
                new ControllerInfo(TrackedKind.Controller, Hand.Left, "LHR-AAAA", 87, true),
                new ControllerInfo(TrackedKind.Controller, Hand.Right, "LHR-BBBB", 50),
            ],
            controllers);
    }

    [Fact]
    public void A_controller_libmonado_doesnt_list_keeps_its_serial_alone()
    {
        var controllers = DeviceStatus.Controllers([new TrackedDevice(TrackedKind.Tracker, "LHR-CCCC")], []);

        Assert.Equal([new ControllerInfo(TrackedKind.Tracker, null, "LHR-CCCC")], controllers);
    }

    [Fact]
    public void Controllers_the_log_didnt_name_are_listed_without_hand_tracking_duplicates()
    {
        var controllers = DeviceStatus.Controllers(null, [
            Device(0, "head", "Headset"),
            Device(1, "eyes", "Eye tracker"),
            Device(2, "gamepad", "Touch Controller (L)"),
            Device(2, "left", "Touch Controller (L)"),
            Device(3, "hand-tracking-unobstructed-left", "Left hand"),
            Device(4, null, "Vive Tracker", "LHR-DDDD"),
        ]);

        Assert.Equal(
            [
                new ControllerInfo(TrackedKind.Controller, Hand.Left, null),
                new ControllerInfo(TrackedKind.Tracker, null, "LHR-DDDD"),
            ],
            controllers);
    }

    [Theory]
    [InlineData("Index Controller L", Hand.Left)]
    [InlineData("Index Controller R", Hand.Right)]
    [InlineData("Touch Controller (R)", Hand.Right)]
    [InlineData("Left Hand Controller", Hand.Left)]
    [InlineData("WMR right controller", Hand.Right)]
    [InlineData("Vive Tracker", null)]
    [InlineData("L", null)]
    [InlineData("Left and right", null)]
    [InlineData(null, null)]
    public void The_hand_comes_from_the_name_without_a_role(string? name, Hand? hand) =>
        Assert.Equal(hand, DeviceStatus.HandOf([], name));

    [Fact]
    public void The_role_wins_over_the_name() =>
        Assert.Equal(Hand.Right, DeviceStatus.HandOf(["gamepad", "right"], "Index Controller L"));
}
