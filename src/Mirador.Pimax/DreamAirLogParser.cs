using Mirador.Core.Modules;

namespace Mirador.Pimax;

public sealed class DreamAirLogParser
{
    public const string EyeKey = "eye";
    public const string HeadsetKey = "headset";

    private readonly object gate = new();
    private readonly string headset;
    private readonly bool headsetThere;
    private readonly StatusLine? eye;
    private StatusLine? noDriver;

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

    public bool Feed(string line)
    {
        lock (gate)
        {
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
