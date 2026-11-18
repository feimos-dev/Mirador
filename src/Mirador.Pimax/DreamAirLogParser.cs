using System.Globalization;
using System.Text.RegularExpressions;
using Mirador.Core.Modules;

namespace Mirador.Pimax;

/// <summary>
/// Turns Monado's output into a Pimax headset's status lines on Home: the display, from the lines of Mirador's layer;
/// the eye tracker, from the geometry upload before the run; and the headset, when Monado's prober found no driver
/// for it. Lines are matched by their message, whatever prefix the log puts in front. Fed one line at a time;
/// <see cref="Lines"/> may be read from any thread.
/// </summary>
public sealed partial class DreamAirLogParser
{
    public const string DisplayKey = "display";
    public const string EyeKey = "eye";
    public const string HeadsetKey = "headset";

    /// <summary>How long after construction the layer may take to log <c>display session open</c> before the display counts as failed.</summary>
    public static readonly TimeSpan DisplayWait = TimeSpan.FromSeconds(60);

    private const string Layer = "[dvl] ";

    private readonly object gate = new();
    private readonly TimeProvider time;
    private readonly long startedAt;
    private string? lastLayerMessage;
    private string? failed;
    private readonly string headset;
    private readonly bool headsetThere;
    private readonly StatusLine? eye;

    private string? mode;
    private long presents;
    private long missed;
    private bool sawStats;
    private StatusLine? noDriver;

    /// <param name="upload">How the geometry upload before this run went; null when there was none (then there is no eye line).</param>
    /// <param name="headset">The model's name, for the line that says Monado has no driver for it.</param>
    /// <param name="headsetThere">Whether the headset's board (and Lighthouse receiver) were plugged in at the Start: only
    /// then is a missing head Monado's driver's fault.</param>
    /// <param name="timeProvider">The clock for <see cref="DisplayWait"/>.</param>
    public DreamAirLogParser(
        GeometryResult? upload = null, string headset = "Pimax Dream Air", bool headsetThere = true, TimeProvider? timeProvider = null)
    {
        time = timeProvider ?? TimeProvider.System;
        startedAt = time.GetTimestamp();
        this.headset = headset;
        this.headsetThere = headsetThere;
        eye = upload switch
        {
            null => null,
            { Ok: true } => new StatusLine(EyeKey, "Eye tracker", StatusLevel.Ok, GeometryUploader.Uploaded),
            _ => new StatusLine(EyeKey, "Eye tracker", StatusLevel.Warning, upload.Message),
        };
    }

    /// <summary>The display line, then the eye tracker's when a geometry upload ran, then the headset's once Monado found no driver for it.</summary>
    public IReadOnlyList<StatusLine> Lines
    {
        get
        {
            lock (gate)
            {
                List<StatusLine> lines = [Display()];
                if (eye is not null)
                {
                    lines.Add(eye);
                }

                if (noDriver is not null)
                {
                    lines.Add(noDriver);
                }

                return lines;
            }
        }
    }

    /// <summary>
    /// Why the display didn't come up (no <c>display session open</c> within <see cref="DisplayWait"/> of the start, with
    /// the layer's last message); null while it may still come, and once it did.
    /// </summary>
    public string? Failed
    {
        get
        {
            lock (gate)
            {
                return failed;
            }
        }
    }

    /// <summary>The layer's text without the level name (<c>error</c>, <c>warn</c>, <c>info</c>, <c>debug</c>) it puts in front.</summary>
    private static string WithoutLevel(string text)
    {
        foreach (var level in new[] { "error", "warn", "info", "debug" })
        {
            if (text.StartsWith(level + ": ", StringComparison.Ordinal))
            {
                return text[(level.Length + 2)..];
            }
        }

        return text;
    }

    /// <summary>Looks at the clock: sets <see cref="Failed"/> once the display session hasn't opened within <see cref="DisplayWait"/>.</summary>
    public void Tick()
    {
        lock (gate)
        {
            if (mode is null && failed is null && time.GetElapsedTime(startedAt) > DisplayWait)
            {
                var last = lastLayerMessage is null ? "" : (lastLayerMessage.EndsWith('.') ? lastLayerMessage : lastLayerMessage + ".") + " ";
                failed = $"The headset's display didn't come up within a minute. {last}Check Diagnostics, then start again.";
            }
        }
    }

    /// <summary>Reads one line of Monado's output; whether <see cref="Lines"/> changed.</summary>
    public bool Feed(string line)
    {
        lock (gate)
        {
            var before = (Display(), noDriver);
            Read(line);
            return before != (Display(), noDriver);
        }
    }

    private void Read(string line)
    {
        if (line.IndexOf(Layer, StringComparison.Ordinal) is var at and >= 0)
        {
            lastLayerMessage = WithoutLevel(line[(at + Layer.Length)..].Trim());
        }

        if (DisplayOpen().Match(line) is { Success: true } open)
        {
            var hz = double.Parse(open.Groups["hz"].Value, CultureInfo.InvariantCulture);
            failed = null;
            mode = $"Direct mode, {open.Groups["w"].Value}×{open.Groups["h"].Value} at {hz.ToString("0.##", CultureInfo.InvariantCulture)} Hz";
            return;
        }

        if (Stats().Match(line) is { Success: true } stats)
        {
            presents += long.Parse(stats.Groups["presents"].Value, CultureInfo.InvariantCulture);
            missed += long.Parse(stats.Groups["missed"].Value, CultureInfo.InvariantCulture);
            sawStats = true;
            return;
        }

        // Monado's prober, when none of its drivers takes the headset.
        if (line.Contains("No builder can be used to create a head device", StringComparison.Ordinal))
        {
            noDriver = new StatusLine(
                HeadsetKey,
                "Headset",
                StatusLevel.Error,
                headsetThere
                    ? $"This Monado has no driver for the {headset}: none of its drivers took the headset. Use a profile whose Monado has a driver for it."
                    : "Monado found no headset: plug the headset in and switch it on, then start Monado again.");
        }
    }

    private StatusLine Display()
    {
        if (mode is null)
        {
            return failed is not null
                ? new StatusLine(DisplayKey, "Display", StatusLevel.Error, failed)
                : new StatusLine(DisplayKey, "Display", StatusLevel.Info, "Waiting for the display");
        }

        var text = sawStats
            ? $"{mode}, {missed.ToString("N0", CultureInfo.InvariantCulture)} missed of {presents.ToString("N0", CultureInfo.InvariantCulture)} frames"
            : mode;
        return new StatusLine(DisplayKey, "Display", StatusLevel.Ok, text);
    }

    [GeneratedRegex(@"display session open: .*?, (?<w>\d+)x(?<h>\d+) at (?<hz>\d+(?:\.\d+)?) Hz", RegexOptions.CultureInvariant)]
    private static partial Regex DisplayOpen();

    [GeneratedRegex(@"(?<presents>\d+) presents in [\d.]+ s .*?(?<missed>\d+) missed \(", RegexOptions.CultureInvariant)]
    private static partial Regex Stats();
}
