using System.Diagnostics;
using System.Globalization;

namespace Mirador.Ipc;

public sealed record XrDevice(string Label, int? Percent, bool Charging);

public enum XrExit
{
    Monado,

    Game,

    Nothing,
}

public static class XrLink
{
    public const string HomeRole = "home";

    public const string DashboardRole = "dashboard";

    public const string HomeAppName = "Mirador Home";
    public const string DashboardAppName = "Mirador Dashboard";

    public const string Hello = "HELLO";

    public const string Running = "RUNNING";

    public const string Shown = "SHOWN";

    public const string Toggle = "TOGGLE";

    public const string Recentre = "RECENTRE";

    public const string Exit = "EXIT";

    public const string Open = "OPEN";

    public const string Devices = "DEVICES";

    public const string Exits = "EXITS";

    public const string Recentred = "RECENTRED";

    public const string Quit = "QUIT";

    public const int MaxDevices = 12;

    public static string NameFor(int sessionId) => $"mirador-xr-{sessionId}";

    public static string CurrentName()
    {
        using var self = Process.GetCurrentProcess();
        return NameFor(self.SessionId);
    }

    public static (string Word, string Value) Split(string line)
    {
        var space = line.IndexOf(' ');
        return space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
    }

    public static string Flag(string word, bool on) => word + (on ? " 1" : " 0");

    public static bool? ParseFlag(string rest) => rest switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };

    public static string FormatRecentre(ReadOnlySpan<float> pose)
    {
        if (pose.Length != 7)
        {
            throw new ArgumentException("A pose is seven numbers.", nameof(pose));
        }

        var parts = new string[8];
        parts[0] = Recentre;
        for (var i = 0; i < 7; i++)
        {
            parts[i + 1] = pose[i].ToString("R", CultureInfo.InvariantCulture);
        }

        return string.Join(' ', parts);
    }

    public static float[]? ParsePose(string rest)
    {
        var parts = rest.Split(' ');
        if (parts.Length != 7)
        {
            return null;
        }

        var pose = new float[7];
        for (var i = 0; i < 7; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out pose[i]) || !float.IsFinite(pose[i]))
            {
                return null;
            }
        }

        return pose;
    }

    public static string FormatDevices(IEnumerable<XrDevice> devices)
    {
        var entries = devices.Take(MaxDevices).Select(d =>
            $"{Label(d.Label)},{d.Percent?.ToString(CultureInfo.InvariantCulture)},{(d.Charging ? '1' : '0')}");
        var line = Devices + " " + string.Join('|', entries);
        return line.TrimEnd();
    }

    public static IReadOnlyList<XrDevice> ParseDevices(string rest)
    {
        var devices = new List<XrDevice>();
        foreach (var entry in rest.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(',');
            if (parts.Length != 3 || parts[0].Length == 0 || devices.Count == MaxDevices)
            {
                continue;
            }

            int? percent = null;
            if (parts[1].Length > 0)
            {
                if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > 100)
                {
                    continue;
                }

                percent = value;
            }

            devices.Add(new XrDevice(parts[0], percent, parts[2] == "1"));
        }

        return devices;
    }

    public static string FormatExits(XrExit exit) => Exits + " " + exit switch
    {
        XrExit.Monado => "monado",
        XrExit.Game => "game",
        _ => "nothing",
    };

    public static XrExit ParseExits(string rest) => rest switch
    {
        "monado" => XrExit.Monado,
        "game" => XrExit.Game,
        _ => XrExit.Nothing,
    };

    private static string Label(string label)
    {
        var clean = new string(label.Where(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')).Take(8).ToArray());
        return clean.Length == 0 ? "?" : clean;
    }
}
