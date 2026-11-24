using System.Diagnostics;
using System.Globalization;

namespace Mirador.Ipc;

/// <summary>A controller or tracker as the dashboard's bar shows it.</summary>
/// <param name="Label">"L" or "R" for a hand's controller, else what tells the device apart (the end of its serial).</param>
/// <param name="Percent">Its battery's charge from 0 to 100; null when it reports none.</param>
/// <param name="Charging">Whether the battery is charging.</param>
public sealed record XrDevice(string Label, int? Percent, bool Charging);

/// <summary>What the dashboard's exit button would end.</summary>
public enum XrExit
{
    /// <summary>No game is shown: it stops Monado.</summary>
    Monado,

    /// <summary>A game is shown that Mirador can end.</summary>
    Game,

    /// <summary>A game is shown that Mirador can't end: no button.</summary>
    Nothing,
}

/// <summary>
/// The pipe between a running Mirador and its two programs in the headset, the home and the dashboard
/// (<c>Mirador.Xr.exe</c>), and the lines they exchange. Each program connects once and keeps the connection for its
/// life; either side sends a line whenever it has something to say. Lines are printable ASCII ended by <c>\n</c>, at
/// most <see cref="AppPipe.MaxLineBytes"/> bytes, a word and then its values separated by single spaces.
/// </summary>
public static class XrLink
{
    /// <summary>The home: a scene app that draws the floor grid, shown whenever no game is.</summary>
    public const string HomeRole = "home";

    /// <summary>The dashboard: an overlay app drawn above every app.</summary>
    public const string DashboardRole = "dashboard";

    /// <summary>The OpenXR application names of the two programs, by which Mirador knows them among Monado's apps.</summary>
    public const string HomeAppName = "Mirador Home";
    public const string DashboardAppName = "Mirador Dashboard";

    /// <summary>A program's first line: <c>HELLO home</c> or <c>HELLO dashboard</c>.</summary>
    public const string Hello = "HELLO";

    /// <summary>From either program, once: its OpenXR session has begun.</summary>
    public const string Running = "RUNNING";

    /// <summary>From the home, <c>SHOWN 1</c> or <c>SHOWN 0</c>: whether it is the app Monado shows.</summary>
    public const string Shown = "SHOWN";

    /// <summary>From the dashboard: the system button was pressed and let go.</summary>
    public const string Toggle = "TOGGLE";

    /// <summary>From the dashboard, <c>RECENTRE qx qy qz qw x y z</c>: put the origin under the head, whose pose in the stage follows.</summary>
    public const string Recentre = "RECENTRE";

    /// <summary>From the dashboard: its exit button was pressed (and confirmed, where it asks).</summary>
    public const string Exit = "EXIT";

    /// <summary>To the dashboard, <c>OPEN 1</c> or <c>OPEN 0</c>: show or hide itself.</summary>
    public const string Open = "OPEN";

    /// <summary>To the dashboard, <c>DEVICES L,82,1|R,64,0|4F2A,,0</c>: the controllers and trackers (label, percent, charging).</summary>
    public const string Devices = "DEVICES";

    /// <summary>To the dashboard, <c>EXITS monado</c>, <c>EXITS game</c> or <c>EXITS nothing</c>: what its exit button would end.</summary>
    public const string Exits = "EXITS";

    /// <summary>To the dashboard, <c>RECENTRED 1</c> or <c>RECENTRED 0</c>: whether the origin moved as asked.</summary>
    public const string Recentred = "RECENTRED";

    /// <summary>To either program: end the session and exit.</summary>
    public const string Quit = "QUIT";

    /// <summary>The most devices a <see cref="Devices"/> line names; the rest don't fit a line.</summary>
    public const int MaxDevices = 12;

    /// <summary>The pipe's name for Windows session <paramref name="sessionId"/>; the full name is <c>\\.\pipe\mirador-xr-&lt;id&gt;</c>.</summary>
    public static string NameFor(int sessionId) => $"mirador-xr-{sessionId}";

    /// <summary>The pipe's name for this process's Windows session.</summary>
    public static string CurrentName()
    {
        using var self = Process.GetCurrentProcess();
        return NameFor(self.SessionId);
    }

    /// <summary>The line's word and what follows its first space ("" when nothing does).</summary>
    public static (string Word, string Value) Split(string line)
    {
        var space = line.IndexOf(' ');
        return space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
    }

    /// <summary><paramref name="word"/> with a flag: <c>OPEN 1</c>.</summary>
    public static string Flag(string word, bool on) => word + (on ? " 1" : " 0");

    /// <summary>The flag after a word: true for "1", false for "0", null for anything else.</summary>
    public static bool? ParseFlag(string rest) => rest switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };

    /// <summary>A <see cref="Recentre"/> line for a head at <paramref name="pose"/> (orientation x, y, z, w, then position).</summary>
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

    /// <summary>The seven numbers after <see cref="Recentre"/>; null unless they are seven finite numbers.</summary>
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

    /// <summary>A <see cref="Devices"/> line for the first <see cref="MaxDevices"/> of <paramref name="devices"/>.</summary>
    public static string FormatDevices(IEnumerable<XrDevice> devices)
    {
        var entries = devices.Take(MaxDevices).Select(d =>
            $"{Label(d.Label)},{d.Percent?.ToString(CultureInfo.InvariantCulture)},{(d.Charging ? '1' : '0')}");
        var line = Devices + " " + string.Join('|', entries);
        return line.TrimEnd();
    }

    /// <summary>The devices after <see cref="Devices"/>; an entry that can't be read is left out.</summary>
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

    /// <summary>An <see cref="Exits"/> line.</summary>
    public static string FormatExits(XrExit exit) => Exits + " " + exit switch
    {
        XrExit.Monado => "monado",
        XrExit.Game => "game",
        _ => "nothing",
    };

    /// <summary>What follows <see cref="Exits"/>; anything unknown is <see cref="XrExit.Nothing"/>.</summary>
    public static XrExit ParseExits(string rest) => rest switch
    {
        "monado" => XrExit.Monado,
        "game" => XrExit.Game,
        _ => XrExit.Nothing,
    };

    /// <summary>A label that can't break a line: letters and digits only, eight at most.</summary>
    private static string Label(string label)
    {
        var clean = new string(label.Where(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')).Take(8).ToArray());
        return clean.Length == 0 ? "?" : clean;
    }
}
