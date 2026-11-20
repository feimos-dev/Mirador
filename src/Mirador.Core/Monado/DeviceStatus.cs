using System.Globalization;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Monado;

/// <summary>Which hand a controller is for.</summary>
public enum Hand
{
    Left,
    Right,
}

/// <summary>One controller or tracker on Home's card.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Hand">Its hand, when Monado's role or its name says; null otherwise.</param>
/// <param name="Serial">Its serial, when known.</param>
/// <param name="Battery">Its charge in percent, when it reports one.</param>
/// <param name="Charging">Whether it is charging.</param>
public sealed record ControllerInfo(TrackedKind Kind, Hand? Hand, string? Serial, int? Battery = null, bool Charging = false);

/// <summary>The devices Monado created: the headset's lines for Home's status, the rest for its controllers card.</summary>
public static class DeviceStatus
{
    /// <summary>The roles listed in the status, in this order, with each one's label.</summary>
    private static readonly (string Role, string Label)[] StatusRoles =
    [
        ("head", "Headset"),
        ("eyes", "Eye tracker"),
    ];

    /// <summary>
    /// One <see cref="StatusLevel.Ok"/> line for the headset and one for the eye tracker, in that order. A device given
    /// under two roles is listed once, under the first. The text is the device's name, then its serial and its battery
    /// when it has them ("Pimax Dream Air, serial XR5DA-P501"). Controllers and trackers are left to <see cref="Controllers"/>.
    /// </summary>
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

    /// <summary>
    /// The controllers and trackers for Home's card: each one the Lighthouse log named (<paramref name="tracked"/>), with
    /// its hand and battery from the libmonado device of the same serial; then libmonado's controllers, gamepads and
    /// devices without a role that the log didn't name. Hand-tracking devices are the controllers' own, so they aren't
    /// listed again; neither are the headset and eye tracker (<see cref="Lines"/>).
    /// </summary>
    public static IReadOnlyList<ControllerInfo> Controllers(IReadOnlyList<TrackedDevice>? tracked, IReadOnlyList<MonadoDevice> devices)
    {
        // A device can come under several roles: one entry per device, with all its roles.
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
                && string.Equals(d.Device.Serial?.Trim(), device.Serial, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// The hand Monado gave the device (role <c>left</c> or <c>right</c>), else the one its name ends in: "L" or "R"
    /// as a word ("Index Controller L"), or "Left" or "Right" anywhere as a word.
    /// </summary>
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
