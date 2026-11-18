using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Tests.Monado;

public class DeviceStatusTests
{
    private static MonadoDevice Device(int index, string? role, string name, bool battery = false, bool charging = false, float charge = 0) =>
        new(index, role, name, null, 0, battery, charging, charge);

    [Fact]
    public void Lines_list_devices_by_role_with_battery()
    {
        var lines = DeviceStatus.Lines([
            Device(3, "right", "Index Controller R", battery: true, charge: 0.5f),
            Device(0, "head", "Pimax Dream Air"),
            Device(4, "hand-tracking-unobstructed-right", "Right hand tracker"),
            Device(1, "eyes", "Eye tracker"),
            Device(2, "left", "Index Controller L", battery: true, charging: true, charge: 0.874f),
            Device(5, "gamepad", "Xbox controller", battery: true, charge: 1f),
            Device(6, "hand-tracking-unobstructed-left", "Left hand tracker"),
        ]);

        Assert.Equal(
            [
                new StatusLine("device-0", "Headset", StatusLevel.Ok, "Pimax Dream Air"),
                new StatusLine("device-1", "Eye tracker", StatusLevel.Ok, "Eye tracker"),
                new StatusLine("device-2", "Left controller", StatusLevel.Ok, "Index Controller L, 87% (charging)"),
                new StatusLine("device-3", "Right controller", StatusLevel.Ok, "Index Controller R, 50%"),
                new StatusLine("device-5", "Gamepad", StatusLevel.Ok, "Xbox controller, 100%"),
                new StatusLine("device-6", "Left hand", StatusLevel.Ok, "Left hand tracker"),
                new StatusLine("device-4", "Right hand", StatusLevel.Ok, "Right hand tracker"),
            ],
            lines);
    }

    [Fact]
    public void A_device_in_two_roles_is_listed_once()
    {
        var lines = DeviceStatus.Lines([Device(2, "gamepad", "Index Controller L"), Device(2, "left", "Index Controller L")]);

        Assert.Equal([new StatusLine("device-2", "Left controller", StatusLevel.Ok, "Index Controller L")], lines);
    }

    [Fact]
    public void Devices_without_a_role_come_last()
    {
        var lines = DeviceStatus.Lines([
            Device(2, null, "Tracker B"),
            Device(1, null, "Tracker A"),
            Device(0, "head", "Headset"),
        ]);

        Assert.Equal(["device-0", "device-1", "device-2"], lines.Select(l => l.Key));
        Assert.Equal(["Headset", "Device", "Device"], lines.Select(l => l.Label));
        Assert.Equal(["Headset", "Tracker A", "Tracker B"], lines.Select(l => l.Text));
    }

    [Fact]
    public void No_devices_give_no_lines() => Assert.Empty(DeviceStatus.Lines([]));
}
