using Mirador.Core.Modules;
using Mirador.Core.Profiles;

namespace Mirador.DirectMode;

/// <summary>
/// The direct-mode layer while Monado runs: the layer's variables, the display's status line from the layer's output,
/// and a stop when the display never comes up.
/// </summary>
public sealed class DirectModeSession : ModuleSession, IDisposable
{
    /// <summary>How often the session looks whether the display has come up in time.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly DirectModeLogParser parser;
    private readonly object gate = new();
    private readonly ITimer timer;
    private SessionWarning? pending;
    private bool ended;
    private bool stopRequested;

    /// <param name="environment">The variables Monado gets for the layer.</param>
    /// <param name="notUsed">Why the layer leaves this headset to Windows; null when it drives the display.</param>
    /// <param name="time">The clock for the display's wait.</param>
    public DirectModeSession(IReadOnlyList<EnvVar> environment, string? notUsed, TimeProvider time)
    {
        Environment = environment;
        pending = notUsed is null ? null : new SessionWarning("direct-mode", notUsed);
        parser = new DirectModeLogParser(time, layerUsed: notUsed is null);
        timer = time.CreateTimer(_ => Poll(), null, PollInterval, Timeout.InfiniteTimeSpan);
    }

    public override IReadOnlyList<EnvVar> Environment { get; }

    public override IReadOnlyDictionary<string, string> RecoveryData { get; } = new Dictionary<string, string>();

    public override IReadOnlyList<StatusLine> Status => [parser.Line];

    public override void OnServiceLine(string line)
    {
        if (parser.Feed(line))
        {
            RaiseStatusChanged();
        }
    }

    public override Task EndAsync(CancellationToken ct)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (gate)
        {
            ended = true;
        }

        timer.Dispose();
    }

    /// <summary>Raises the warning from getting ready (Mirador listens only once it has the session), and stops Monado once the display is late.</summary>
    private void Poll()
    {
        SessionWarning? warning;
        lock (gate)
        {
            if (ended)
            {
                return;
            }

            warning = pending;
            pending = null;
            timer.Change(PollInterval, Timeout.InfiniteTimeSpan);
        }

        if (warning is not null)
        {
            RaiseWarning(warning);
        }

        parser.Tick();
        if (parser.Failed is not { } failed)
        {
            return;
        }

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
