using Mirador.Core;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Pimax;

/// <summary>
/// A Dream Air session while Monado runs: the fixed environment, the status lines (the eye geometry upload, and Monado's
/// missing driver), and a watch for programs that would take the headset or the receiver back.
/// </summary>
public sealed class DreamAirSession : ModuleSession, IDisposable
{
    /// <summary>How often the session looks for Pimax's software and SteamVR.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    internal const string PimaxStarted =
        "Pimax's software started while Monado runs and may take the headset back. Close Pimax Play.";

    internal const string SteamVrStarted =
        "SteamVR started while Monado runs. Close it: it competes with Monado for the Lighthouse receiver.";

    private readonly ModuleContext ctx;
    private readonly DreamAirLogParser parser;
    private readonly object gate = new();
    private readonly ITimer timer;
    private List<SessionWarning>? pending;
    private bool pimaxSeen;
    private bool steamVrSeen;
    private bool ended;
    private bool stopRequested;

    /// <param name="ctx">The module's context: the helper, the process list and the clock.</param>
    /// <param name="environment">The variables Monado gets for the Dream Air.</param>
    /// <param name="upload">How the eye geometry upload went, which the eye tracker's line shows; null when there was none (no eye line).</param>
    /// <param name="warnings">Warnings from getting ready, raised at the first look (Mirador listens only once it has the session).</param>
    public DreamAirSession(
        ModuleContext ctx, IReadOnlyList<EnvVar> environment, GeometryResult? upload,
        IReadOnlyList<SessionWarning>? warnings = null, string headset = "Pimax Dream Air", bool headsetThere = true)
    {
        this.ctx = ctx;
        pending = warnings is { Count: > 0 } ? [.. warnings] : null;
        Environment = environment;
        parser = new DreamAirLogParser(upload, headset, headsetThere);
        timer = ctx.Time.CreateTimer(_ => Poll(), null, PollInterval, Timeout.InfiniteTimeSpan);
    }

    public override IReadOnlyList<EnvVar> Environment { get; }

    public override IReadOnlyDictionary<string, string> RecoveryData { get; } = new Dictionary<string, string>();

    public override IReadOnlyList<StatusLine> Status => parser.Lines;

    public override void OnServiceLine(string line)
    {
        if (parser.Feed(line))
        {
            RaiseStatusChanged();
        }

        // Monado found no driver for the headset: it idles, so it stops now.
        StopIfFailed();
    }

    /// <summary>Stops watching for Pimax's software and SteamVR.</summary>
    public override Task EndAsync(CancellationToken ct)
    {
        StopWatching();
        return Task.CompletedTask;
    }

    public void Dispose() => StopWatching();

    private void StopWatching()
    {
        lock (gate)
        {
            ended = true;
        }

        timer.Dispose();
    }

    /// <summary>One look at the running programs; warns once each time Pimax's software or SteamVR appears.</summary>
    private void Poll()
    {
        IReadOnlyList<ProcInfo> running;
        try
        {
            running = ctx.Processes.Snapshot();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            running = [];
        }

        var pimax = running.Any(p =>
            string.Equals(p.Name, PimaxNames.Service, StringComparison.OrdinalIgnoreCase)
            || PimaxNames.Processes.Contains(p.Name, StringComparer.OrdinalIgnoreCase));
        var steamVr = LighthouseConflicts.Find(running).Count > 0;

        var warnings = new List<SessionWarning>();
        lock (gate)
        {
            if (ended)
            {
                return;
            }

            if (pending is not null)
            {
                warnings.AddRange(pending);
                pending = null;
            }

            if (pimax && !pimaxSeen)
            {
                warnings.Add(new SessionWarning("pimax", PimaxStarted));
            }

            if (steamVr && !steamVrSeen)
            {
                warnings.Add(new SessionWarning("lighthouse", SteamVrStarted));
            }

            pimaxSeen = pimax;
            steamVrSeen = steamVr;
            timer.Change(PollInterval, Timeout.InfiniteTimeSpan);
        }

        foreach (var warning in warnings)
        {
            RaiseWarning(warning);
        }
    }

    /// <summary>Asks Mirador once to stop Monado when the parser says it can't work.</summary>
    private void StopIfFailed()
    {
        if (parser.Failed is { } failed)
        {
            lock (gate)
            {
                if (stopRequested || ended)
                {
                    return;
                }

                stopRequested = true;
            }

            RaiseStatusChanged();
            RaiseStopRequested(failed);
        }
    }
}
