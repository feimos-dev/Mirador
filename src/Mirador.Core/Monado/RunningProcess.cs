using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mirador.Core.Monado;

internal sealed class RunningProcess : IRunningProcess
{
    private const int MaxEarlyLines = 1000;

    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan DisposeLimit = TimeSpan.FromSeconds(5);

    private readonly Process process;
    private readonly Action<Process> terminate;
    private readonly Func<int, bool>? exitKey;

    private readonly SafeHandle job;

    private readonly object gate = new();
    private readonly object lifecycle = new();
    private Action<string>? listeners;
    private List<string>? early = [];
    private bool disposed;

    public RunningProcess(Process started, SafeHandle job, Action<Process>? terminate = null, Func<int, bool>? exitKey = null)
    {
        this.job = job;
        this.exitKey = exitKey;
        this.terminate = terminate ?? (p => p.Kill(entireProcessTree: true));
        process = started;
        Id = started.Id;
        var output = Task.WhenAll(PumpAsync(started.StandardOutput), PumpAsync(started.StandardError));
        Exited = AwaitExitAsync(output);
    }

    public int Id { get; }

    public Task<int> Exited { get; }

    public event Action<Exception>? ListenerFailed;

    public event Action<string>? Line
    {
        add
        {
            if (value is null)
            {
                return;
            }

            List<Exception>? failures = null;
            lock (gate)
            {
                listeners += value;
                if (early is { } missed)
                {
                    early = null;
                    foreach (var text in missed)
                    {
                        Deliver(value, text, ref failures);
                    }
                }
            }

            Report(failures);
        }
        remove
        {
            lock (gate)
            {
                listeners -= value;
            }
        }
    }

    public void Kill()
    {
        lock (lifecycle)
        {
            if (!disposed)
            {
                KillCore();
            }
        }
    }

    public async Task<bool> EndCleanlyAsync(TimeSpan limit)
    {
        lock (lifecycle)
        {
            if (disposed || exitKey is null)
            {
                return Exited.IsCompleted;
            }
        }

        if (Exited.IsCompleted)
        {
            return true;
        }

        try
        {
            if (!await Task.Run(() => exitKey(Id)).ConfigureAwait(false))
            {
                return false;
            }

            await Exited.WaitAsync(limit).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or Win32Exception)
        {
            return Exited.IsCompleted;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        try
        {
            KillCore();
            await Exited.WaitAsync(DisposeLimit).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        if (Exited.IsCompleted)
        {
            ReleaseProcess();
        }
        else
        {
            _ = Exited.ContinueWith(_ => ReleaseProcess(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        GC.KeepAlive(job);
    }

    private void ReleaseProcess()
    {
        lock (lifecycle)
        {
            process.Dispose();
        }
    }

    private void KillCore()
    {
        try
        {
            terminate(process);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or AggregateException or ObjectDisposedException)
        {
            if (!HasEnded())
            {
                throw;
            }
        }
    }

    private bool HasEnded()
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private async Task<int> AwaitExitAsync(Task output)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        var code = process.ExitCode;
        try
        {
            await output.WaitAsync(DrainGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        return code;
    }

    private async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } text)
            {
                Publish(text);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }

    private void Publish(string text)
    {
        List<Exception>? failures = null;
        lock (gate)
        {
            if (listeners is { } handler)
            {
                Deliver(handler, text, ref failures);
            }
            else if (early is { Count: < MaxEarlyLines })
            {
                early.Add(text);
            }
        }

        Report(failures);
    }

    private static void Deliver(Action<string> handler, string text, ref List<Exception>? failures)
    {
        foreach (var each in handler.GetInvocationList())
        {
            try
            {
                ((Action<string>)each)(text);
            }
            catch (Exception e)
            {
                (failures ??= []).Add(e);
            }
        }
    }

    private void Report(List<Exception>? failures)
    {
        if (failures is null || ListenerFailed is not { } handler)
        {
            return;
        }

        foreach (var failure in failures)
        {
            foreach (var each in handler.GetInvocationList())
            {
                try
                {
                    ((Action<Exception>)each)(failure);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
