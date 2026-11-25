using System.Globalization;
using System.Text.RegularExpressions;
using Mirador.Core.Modules;

namespace Mirador.DirectMode;

public sealed partial class DirectModeLogParser
{
    public const string DisplayKey = "display";

    public static readonly TimeSpan DisplayWait = TimeSpan.FromSeconds(60);

    private const string Layer = "[dvl] ";

    private readonly object gate = new();
    private readonly TimeProvider time;
    private readonly long startedAt;
    private readonly bool layerUsed;
    private string? lastLayerMessage;
    private string? failed;
    private string? mode;
    private long presents;
    private long missed;
    private bool sawStats;

    public DirectModeLogParser(TimeProvider? timeProvider = null, bool layerUsed = true)
    {
        time = timeProvider ?? TimeProvider.System;
        startedAt = time.GetTimestamp();
        this.layerUsed = layerUsed;
    }

    public StatusLine Line
    {
        get
        {
            lock (gate)
            {
                return Display();
            }
        }
    }

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

    public void Tick()
    {
        lock (gate)
        {
            if (layerUsed && mode is null && failed is null && time.GetElapsedTime(startedAt) > DisplayWait)
            {
                var last = lastLayerMessage is null ? "" : (lastLayerMessage.EndsWith('.') ? lastLayerMessage : lastLayerMessage + ".") + " ";
                failed = $"The headset's display didn't come up within a minute. {last}Check Diagnostics, then start again.";
            }
        }
    }

    public bool Feed(string line)
    {
        lock (gate)
        {
            var before = Display();
            Read(line);
            return before != Display();
        }
    }

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
