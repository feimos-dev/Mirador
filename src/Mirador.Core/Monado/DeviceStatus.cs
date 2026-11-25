using System.Globalization;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Monado;

public enum Hand
{
    Left,
    Right,
}

public sealed record ControllerInfo(TrackedKind Kind, Hand? Hand, string? Serial, int? Battery = null, bool Charging = false);

public static class DeviceStatus
{
    private static readonly (string Role, string Label)[] StatusRoles =
    [
        ("head", "Headset"),
        ("eyes", "Eye tracker"),
    ];

    public static IReadOnlyList<StatusLine> Lines(IReadOnlyList<MonadoDevice> devices)
    {
        var listed = new HashSet<int>();
        var lines = new List<StatusLine>();
        foreach (var (role, label) in StatusRoles)
        {
            foreach (var device in devices.Where(d => d.Role == role).OrderBy(d => d.Index))
            {
                if (listed.Add(device.Index))
                {
                    lines.Add(Line(device, label));
                }
            }
        }

        return lines;
    }

    public static IReadOnlyList<ControllerInfo> Controllers(IReadOnlyList<TrackedDevice>? tracked, IReadOnlyList<MonadoDevice> devices)
    {
        var byIndex = devices
            .GroupBy(d => d.Index)
            .OrderBy(g => g.Key)
            .Select(g => (Device: g.First(), Roles: g.Select(d => d.Role).ToList()))
            .Where(d => !d.Roles.Any(r => r is "head" or "eyes"))
            .ToList();
        var used = new HashSet<int>();
        var list = new List<ControllerInfo>();
        foreach (var device in tracked ?? [])
        {
            var match = byIndex.FirstOrDefault(d => !used.Contains(d.Device.Index) && !string.IsNullOrWhiteSpace(device.Serial)
                && string.Equals(d.Device.Serial?.Trim(), device.Serial.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match.Device is { } found)
            {
                used.Add(found.Index);
                list.Add(Info(device.Kind, found, match.Roles, device.Serial));
            }
            else
            {
                list.Add(new ControllerInfo(device.Kind, null, device.Serial));
            }
        }

        foreach (var (device, roles) in byIndex.Where(d => !used.Contains(d.Device.Index)))
        {
            if (roles.Any(r => r is "left" or "right" or "gamepad"))
            {
                list.Add(Info(TrackedKind.Controller, device, roles, device.Serial));
            }
            else if (roles.All(r => r is null))
            {
                list.Add(Info(TrackedKind.Tracker, device, roles, device.Serial));
            }
        }

        return list;
    }

    public static Hand? HandOf(IReadOnlyList<string?> roles, string? name)
    {
        if (roles.Contains("left"))
        {
            return Hand.Left;
        }

        if (roles.Contains("right"))
        {
            return Hand.Right;
        }

        var words = (name ?? "").Split([' ', '(', ')', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words[^1] is "L" or "R")
        {
            return words[^1] == "L" ? Hand.Left : Hand.Right;
        }

        var left = words.Any(w => w.Equals("left", StringComparison.OrdinalIgnoreCase));
        var right = words.Any(w => w.Equals("right", StringComparison.OrdinalIgnoreCase));
        return left == right ? null : left ? Hand.Left : Hand.Right;
    }

    private static ControllerInfo Info(TrackedKind kind, MonadoDevice device, IReadOnlyList<string?> roles, string? serial) =>
        new(kind, HandOf(roles, device.Name), string.IsNullOrWhiteSpace(serial) ? null : serial.Trim(),
            device.HasBattery ? Percent(device.Charge) : null, device.HasBattery && device.Charging);

    private static int Percent(float charge) => (int)Math.Round(Math.Clamp(charge, 0f, 1f) * 100, MidpointRounding.AwayFromZero);

    private static StatusLine Line(MonadoDevice device, string label)
    {
        var text = device.Name;
        if (!string.IsNullOrWhiteSpace(device.Serial))
        {
            text += ", serial " + device.Serial;
        }

        if (device.HasBattery)
        {
            text += string.Create(CultureInfo.InvariantCulture, $", {Percent(device.Charge)}%") + (device.Charging ? " (charging)" : "");
        }

        return new StatusLine(string.Create(CultureInfo.InvariantCulture, $"device-{device.Index}"), label, StatusLevel.Ok, text);
    }
}
