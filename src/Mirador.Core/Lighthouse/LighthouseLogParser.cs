using System.Text.RegularExpressions;

namespace Mirador.Core.Lighthouse;

public enum TrackedKind
{
    Controller,

    Tracker,
}

public sealed record TrackedDevice(TrackedKind Kind, string Serial);

public sealed partial class LighthouseLogParser
{
    private readonly HashSet<string> seen = [];
    private readonly List<TrackedDevice> found = [];
    private volatile string? hmdSerial;
    private volatile IReadOnlyList<TrackedDevice> devices = [];

    public string? HmdSerial => hmdSerial;

    public IReadOnlyList<TrackedDevice> Devices => devices;

    public bool Feed(string line)
    {
        if (HmdLine().Match(line) is { Success: true } hmd)
        {
            var serial = hmd.Groups["serial"].Value;
            if (serial == hmdSerial)
            {
                return false;
            }

            hmdSerial = serial;
            return true;
        }

        if (DeviceLine().Match(line) is { Success: true } device)
        {
            var serial = device.Groups["serial"].Value;
            if (!seen.Add(serial))
            {
                return false;
            }

            var kind = device.Groups["kind"].Value == "controller" ? TrackedKind.Controller : TrackedKind.Tracker;
            found.Add(new TrackedDevice(kind, serial));
            devices = found.ToArray();
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"Found lighthouse HMD: (?<serial>\S+)")]
    private static partial Regex HmdLine();

    [GeneratedRegex(@"Found lighthouse (?<kind>controller|tracker): (?<serial>\S+)")]
    private static partial Regex DeviceLine();
}
