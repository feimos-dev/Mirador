using Mirador.Core;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Pimax;

public sealed class DreamAirSession : ModuleSession, IDisposable
{
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

        StopIfFailed();
    }

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
