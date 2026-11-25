using System.Globalization;

namespace Mirador.Ipc;

public sealed record DesktopPlacement(long Monitor, float[] Pose, float Width, float Height);

public static class DesktopLayerLink
{
    public const string AppName = "Mirador Desktop";

    public const string Show = "SHOW";
    public const string Hide = "HIDE";
    public const string Point = "POINT";

    public static string FormatPoint(bool? left) => $"{Point} {left switch { true => "left", false => "right", null => "none" }}";

    public static bool? ParsePoint(string line) => line switch
    {
        "POINT left" => true,
        "POINT right" => false,
        _ => null,
    };

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
