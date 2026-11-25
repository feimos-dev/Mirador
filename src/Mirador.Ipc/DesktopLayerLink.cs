using System.Globalization;

namespace Mirador.Ipc;

/// <summary>Where the desktop's quad goes: a monitor, the quad's pose in the stage and its size in metres.</summary>
/// <param name="Monitor">The monitor's HMONITOR.</param>
/// <param name="Pose">The quad's pose: orientation x, y, z, w, then position x, y, z, with its picture facing +Z.</param>
public sealed record DesktopPlacement(long Monitor, float[] Pose, float Width, float Height);

/// <summary>
/// The lines the dashboard sends the desktop program (<c>Mirador.Xr.exe --desktop</c>) on its standard input: a
/// <see cref="Show"/> whenever the quad should show a monitor or move, a <see cref="Hide"/> when it should go. The end of
/// its input ends the program. Its standard output is log lines.
/// </summary>
public static class DesktopLayerLink
{
    /// <summary>The desktop program's OpenXR application name, by which Mirador knows it among Monado's apps.</summary>
    public const string AppName = "Mirador Desktop";

    public const string Show = "SHOW";
    public const string Hide = "HIDE";

    public static string FormatShow(DesktopPlacement placement)
    {
        if (placement.Pose.Length != 7)
        {
            throw new ArgumentException("A pose is 7 numbers.", nameof(placement));
        }

        var numbers = placement.Pose.Append(placement.Width).Append(placement.Height)
            .Select(n => n.ToString("R", CultureInfo.InvariantCulture));
        return $"{Show} {placement.Monitor.ToString(CultureInfo.InvariantCulture)} {string.Join(' ', numbers)}";
    }

    /// <summary>A <see cref="Show"/> line's placement; null for any other line, or one that doesn't read.</summary>
    public static DesktopPlacement? ParseShow(string line)
    {
        var parts = line.Split(' ');
        if (parts.Length != 11 || parts[0] != Show
            || !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var monitor))
        {
            return null;
        }

        var numbers = new float[9];
        for (var i = 0; i < numbers.Length; i++)
        {
            if (!float.TryParse(parts[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) || !float.IsFinite(numbers[i]))
            {
                return null;
            }
        }

        return numbers[7] > 0 && numbers[8] > 0 ? new DesktopPlacement(monitor, numbers[..7], numbers[7], numbers[8]) : null;
    }
}
