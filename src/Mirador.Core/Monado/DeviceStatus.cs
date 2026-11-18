using System.Globalization;
using Mirador.Core.Modules;
using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Monado;

/// <summary>The devices Monado created, as status lines for Home.</summary>
public static class DeviceStatus
{
    /// <summary>The roles listed, in this order, with each one's label.</summary>
    private static readonly (string Role, string Label)[] Roles =
    [
        ("head", "Headset"),
        ("eyes", "Eye tracker"),
        ("left", "Left controller"),
        ("right", "Right controller"),
        ("gamepad", "Gamepad"),
        ("hand-tracking-unobstructed-left", "Left hand"),
        ("hand-tracking-unobstructed-right", "Right hand"),
    ];

    private const string NoRoleLabel = "Device";

    /// <summary>
    /// One <see cref="StatusLevel.Ok"/> line per device: those with a role in role order, then the others in index
    /// order. A device given under two roles is listed once, under the first. The text is the device's name, with its
    /// battery ("Index Controller L, 87% (charging)") when it has one.
    /// </summary>
    public static IReadOnlyList<StatusLine> Lines(IReadOnlyList<MonadoDevice> devices)
    {
        var listed = new HashSet<int>();
        var lines = new List<StatusLine>();
        foreach (var (role, label) in Roles)
        {
            foreach (var device in devices.Where(d => d.Role == role).OrderBy(d => d.Index))
            {
                if (listed.Add(device.Index))
                {
                    lines.Add(Line(device, label));
                }
            }
        }

        foreach (var device in devices.OrderBy(d => d.Index))
        {
            if (listed.Add(device.Index))
            {
                lines.Add(Line(device, NoRoleLabel));
            }
        }

        return lines;
    }

    private static StatusLine Line(MonadoDevice device, string label)
    {
        var text = device.Name;
        if (device.HasBattery)
        {
            var percent = (int)Math.Round(Math.Clamp(device.Charge, 0f, 1f) * 100, MidpointRounding.AwayFromZero);
            text += string.Create(CultureInfo.InvariantCulture, $", {percent}%") + (device.Charging ? " (charging)" : "");
        }

        return new StatusLine(string.Create(CultureInfo.InvariantCulture, $"device-{device.Index}"), label, StatusLevel.Ok, text);
    }
}
