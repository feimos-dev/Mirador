namespace Mirador.Helper.Tests;

internal sealed class FakeServiceControl : IServiceControl
{
    public bool Installed { get; set; } = true;
    public bool Running { get; set; }

    public bool Stopping { get; set; }
    public Exception? StopFails { get; set; }

    public ManualResetEventSlim? StopGate { get; set; }

    public ManualResetEventSlim StopEntered { get; } = new();
    public List<string> Stopped { get; } = [];
    public List<TimeSpan> StopTimeouts { get; } = [];

    public string? PimaxImagePath { get; set; } = $"\"{Path.Combine(HelperHandlerTests.PimaxRuntime, "PiServiceLauncher.exe")}\"";

    public string? ImagePath(string name) => Installed ? PimaxImagePath : null;

    public (int Id, DateTime Started)? PimaxProcess { get; set; } = (500, new DateTime(2026, 11, 4, 10, 0, 0));

    public (int Id, DateTime Started)? Process(string name) => Installed ? PimaxProcess : null;

    public bool Exists(string name) => Installed;

    public bool IsRunning(string name) => Installed && (Running || Stopping);

    public void Stop(string name, TimeSpan timeout)
    {
        StopEntered.Set();
        StopGate?.Wait();
        if (StopFails is not null)
        {
            throw StopFails;
        }

        Stopped.Add(name);
        StopTimeouts.Add(timeout);
        Running = false;
    }
}

internal sealed class FakeProcessControl : IProcessControl
{
    public List<string> Killed { get; } = [];
    public Exception? KillFails { get; set; }
    public Dictionary<string, string[]> Lines { get; } = [];

    public string? ExecutablePath { get; set; } = HelperHandlerTests.EyeRuntimePath;

    public int? ParentId { get; set; } = 500;

    public DateTime? Started { get; set; } = new DateTime(2026, 11, 4, 10, 0, 5);
    public List<string> Queried { get; } = [];

    public void KillAll(IEnumerable<string> names)
    {
        Killed.AddRange(names);
        if (KillFails is not null)
        {
            throw KillFails;
        }
    }

    public IReadOnlyList<ProcessCommandLine> CommandLines(string processName)
    {
        Queried.Add(processName);
        return Lines.TryGetValue(processName, out var lines) ? [.. lines.Select(l => new ProcessCommandLine(ExecutablePath, l, ParentId, Started))] : [];
    }
}

internal sealed class FakeLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
{
    private int stopRequests;

    public int StopRequests => Volatile.Read(ref stopRequests);

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() => Interlocked.Increment(ref stopRequests);
}

internal sealed class FakeRuntimeRegistry : IRuntimeRegistry
{
    public string? Active { get; set; }
    public string? Recorded { get; set; }

    public HashSet<string> Available { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void SetAvailable(string manifest, bool available)
    {
        if (available)
        {
            Available.Add(manifest);
        }
        else
        {
            Available.Remove(manifest);
        }
    }
    public Exception? Fails { get; set; }
    public int Writes { get; private set; }

    public int MaxConcurrent { get; private set; }

    private int inside;

    public string? GetActive()
    {
        var now = Interlocked.Increment(ref inside);
        lock (this)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, now);
        }

        Thread.Sleep(15);
        Interlocked.Decrement(ref inside);
        return Active;
    }

    public void SetActive(string? path)
    {
        Write();
        Active = path;
    }

    public string? GetRecorded() => Recorded;

    public void SetRecorded(string? path)
    {
        Write();
        Recorded = path;
    }

    private void Write()
    {
        if (Fails is not null)
        {
            throw Fails;
        }

        Writes++;
    }
}

internal sealed class FakeConsentValues : Mirador.Ipc.IConsentValues
{
    public HashSet<string> Sids { get; } = [];
    public Exception? Fails { get; set; }

    public bool Has(string sid) => Sids.Contains(sid);

    public void Set(string sid) => Sids.Add(sid);

    public void Delete(string sid)
    {
        if (Fails is not null)
        {
            throw Fails;
        }

        Sids.Remove(sid);
    }

    public Dictionary<string, string[]> FileLines { get; } = [];

    public string[]? Files(string sid) => FileLines.TryGetValue(sid, out var lines) ? lines : null;

    public void SetFiles(string sid, string[] lines) => FileLines[sid] = lines;

    public void DeleteFiles(string sid) => FileLines.Remove(sid);
}
