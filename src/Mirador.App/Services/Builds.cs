using Mirador.Core;
using Mirador.Core.Build;
using Mirador.Core.Profiles;

namespace Mirador.App.Services;

/// <summary>One build of a profile's branch: its newest line and how it ended.</summary>
public sealed class BuildRun
{
    private volatile string lastLine = "";

    internal CancellationTokenSource Cancel { get; } = new();

    /// <summary>The newest line, or empty before the first; the log file has them all.</summary>
    public string LastLine => lastLine;

    /// <summary>How it ended; null while it runs.</summary>
    public BuildOutcome? Outcome { get; internal set; }

    /// <summary>Whether it was cancelled.</summary>
    public bool Cancelled { get; internal set; }

    private volatile bool approving;

    /// <summary>Whether the build is over but its approval isn't: another build of the profile waits for it.</summary>
    public bool Approving
    {
        get => approving;
        internal set => approving = value;
    }

    /// <summary>Whether it is building, or its approval is open.</summary>
    internal bool Busy => this is { Outcome: null, Cancelled: false } or { Approving: true };

    internal void Add(string line) => lastLine = line;
}

/// <summary>
/// The branch builds of this Mirador, kept here rather than in the Profiles page so that a
/// build goes on while the owner looks at another page. <see cref="Changed"/> is raised on a background thread.
/// </summary>
/// <param name="afterBuilt">
/// Asked with the profile's id after a build that worked: the new build's approval, run once the build's end is told, or
/// null when there is none.
/// </param>
public sealed class Builds(MonadoBuilder builder, Func<string, Func<Task>?>? afterBuilt = null)
{
    private readonly Dictionary<string, BuildRun> runs = [];

    /// <summary>How long a build's new lines wait to be told, so that a burst of them is one change, not hundreds.</summary>
    private static readonly TimeSpan LinesDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>A build started, wrote lines (at most four times a second) or ended: the profile's id.</summary>
    public event Action<string>? Changed;

    /// <summary>The profile's running or last build in this Mirador's run, or null.</summary>
    public BuildRun? RunOf(string profileId)
    {
        lock (runs)
        {
            return runs.GetValueOrDefault(profileId);
        }
    }

    /// <summary>Whether any profile's build is running.</summary>
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

    /// <summary>The newest build log of a profile, from this or an earlier run of Mirador.</summary>
    public string? LastLog(string profileId) => builder.LastLog(profileId);

    /// <summary>Starts building <paramref name="profile"/>'s branch unless it is being built.</summary>
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
        var linesPending = 0; // 1 while a change for new lines is on its way
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
                // Whatever went wrong, the build has ended: the page must not stay on "Building…".
                AppLog.Write("A branch build failed", e);
                run.Add(e.Message);
                outcome = new BuildOutcome(false, e.Message, null, null, builder.LastLog(profile.Id) ?? "");
            }

            // Busy until the approval is done, from before the outcome shows: a new build mustn't write the folder its
            // approval copies.
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

    /// <summary>Cancels the profile's running build; the last good build stays.</summary>
    public void Cancel(string profileId) => RunOf(profileId)?.Cancel.Cancel();
}
