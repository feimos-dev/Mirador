using Mirador.Core.Modules;

namespace Mirador.Pimax;

/// <summary>
/// Turns what a Pimax headset's run did into its status lines on Home: the eye tracker, from the geometry upload before
/// the run; and the headset, when Monado's prober found no driver for it. Lines are matched by their message, whatever
/// prefix the log puts in front. Fed one line at a time; <see cref="Lines"/> may be read from any thread.
/// </summary>
public sealed class DreamAirLogParser
{
    public const string EyeKey = "eye";
    public const string HeadsetKey = "headset";

    private readonly object gate = new();
    private readonly string headset;
    private readonly bool headsetThere;
    private readonly StatusLine? eye;
    private StatusLine? noDriver;

    /// <param name="upload">How the geometry upload before this run went; null when there was none (then there is no eye line).</param>
    /// <param name="headset">The model's name, for the line that says Monado has no driver for it.</param>
    /// <param name="headsetThere">Whether the headset's board (and Lighthouse receiver) were plugged in at the Start: only
    /// then is a missing head Monado's driver's fault.</param>
    public DreamAirLogParser(GeometryResult? upload = null, string headset = "Pimax Dream Air", bool headsetThere = true)
    {
        this.headset = headset;
        this.headsetThere = headsetThere;
        eye = upload switch
        {
            null => null,
            { Ok: true } => new StatusLine(EyeKey, "Eye tracker", StatusLevel.Ok, GeometryUploader.Uploaded),
            _ => new StatusLine(EyeKey, "Eye tracker", StatusLevel.Warning, upload.Message),
        };
    }

    /// <summary>The eye tracker's line when a geometry upload ran, then the headset's once Monado found no driver for it.</summary>
    public IReadOnlyList<StatusLine> Lines
    {
        get
        {
            lock (gate)
            {
                List<StatusLine> lines = [];
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

    /// <summary>Why Monado can't work with the headset: its prober found no driver for it (the headset line's text). Null until then.</summary>
    public string? Failed
    {
        get
        {
            lock (gate)
            {
                return noDriver?.Text;
            }
        }
    }

    /// <summary>Reads one line of Monado's output; whether <see cref="Lines"/> changed.</summary>
    public bool Feed(string line)
    {
        lock (gate)
        {
            // Monado's prober, when none of its drivers takes the headset.
            if (noDriver is not null || !line.Contains("No builder can be used to create a head device", StringComparison.Ordinal))
            {
                return false;
            }

            noDriver = new StatusLine(
                HeadsetKey,
                "Headset",
                StatusLevel.Error,
                headsetThere
                    ? $"This Monado has no driver for the {headset}: none of its drivers took the headset. Use a profile whose Monado has a driver for it."
                    : "Monado found no headset: plug the headset in and switch it on, then start Monado again.");
            return true;
        }
    }
}
