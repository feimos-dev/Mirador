using Mirador.Core;
using Mirador.Core.Build;
using Mirador.Core.Profiles;

namespace Mirador.App.Services;

public sealed class BuildRun
{
    private volatile string lastLine = "";

    internal CancellationTokenSource Cancel { get; } = new();

    public string LastLine => lastLine;

    public BuildOutcome? Outcome { get; internal set; }

    public bool Cancelled { get; internal set; }

    private volatile bool approving;

    public bool Approving
    {
        get => approving;
        internal set => approving = value;
    }

    internal bool Busy => this is { Outcome: null, Cancelled: false } or { Approving: true };

    internal void Add(string line) => lastLine = line;
}

public sealed class Builds(MonadoBuilder builder, Func<string, Func<Task>?>? afterBuilt = null)
{
    private readonly Dictionary<string, BuildRun> runs = [];

    private static readonly TimeSpan LinesDelay = TimeSpan.FromMilliseconds(250);

    public event Action<string>? Changed;

    public BuildRun? RunOf(string profileId)
    {
        lock (runs)
        {
            return runs.GetValueOrDefault(profileId);
        }
    }

    public bool AnyRunning
    {
        get
        {
            lock (runs)
            {
                return runs.Values.Any(r => r.Busy);
            }
        }
    }

    public string? LastLog(string profileId) => builder.LastLog(profileId);

    public void Start(Profile profile)
    {
        BuildRun run;
        lock (runs)
        {
            if (runs.GetValueOrDefault(profile.Id) is { Busy: true })
            {
                return;
            }

            run = new BuildRun();
            runs[profile.Id] = run;
        }

        Changed?.Invoke(profile.Id);
        var linesPending = 0;
        async Task TellLinesAsync()
        {
            await Task.Delay(LinesDelay).ConfigureAwait(false);
            Volatile.Write(ref linesPending, 0);
            Changed?.Invoke(profile.Id);
        }

        _ = Task.Run(async () =>
        {
            BuildOutcome? outcome = null;
            try
            {
                outcome = await builder.BuildAsync(profile, line =>
                {
                    run.Add(line);
                    if (Interlocked.Exchange(ref linesPending, 1) == 0)
                    {
                        _ = TellLinesAsync();
                    }
                }, run.Cancel.Token);
            }
            catch (OperationCanceledException)
            {
                run.Cancelled = true;
            }
            catch (Exception e)
            {
                AppLog.Write("A branch build failed", e);
                run.Add(e.Message);
                outcome = new BuildOutcome(false, e.Message, null, null, builder.LastLog(profile.Id) ?? "");
            }

            var approval = outcome is { Ok: true } ? afterBuilt?.Invoke(profile.Id) : null;
            run.Approving = approval is not null;
            run.Outcome = outcome;
            Changed?.Invoke(profile.Id);
            if (approval is not null)
            {
                try
                {
                    await approval();
                }
                finally
                {
                    run.Approving = false;
                }

                Changed?.Invoke(profile.Id);
            }
        });
    }

    public void Cancel(string profileId) => RunOf(profileId)?.Cancel.Cancel();
}
