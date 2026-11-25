using System.Globalization;
using System.Text;
using Mirador.Ipc;

namespace Mirador.Core.Monado;

public sealed class HelperProcessLauncher(IHelperClient client, ElevatedStart start, string logPath, IProcessLauncher asUser) : IProcessLauncher
{
    public Action<bool, string?>? SteamVrChecked { get; init; }

    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    internal TimeSpan FollowInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    internal TimeSpan StopLimit { get; init; } = TimeSpan.FromSeconds(10);

    public IRunningProcess Start(ProcessSpec spec)
    {
        var reply = client.SendAsync(new HelperRequest(HelperCommand.StartMonadoElevated, start), CancellationToken.None)
            .GetAwaiter().GetResult();
        if (!reply.Ok && reply.Refusal == HelperRefusal.SteamVrChanged)
        {
            SteamVrChecked?.Invoke(true, reply.Error);
            return asUser.Start(spec);
        }

        if (!reply.Ok)
        {
            throw new MiradorException(reply.Error ?? "Mirador's helper service couldn't start Monado as administrator.");
        }

        var id = int.TryParse(reply.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : 0;
        var process = new ElevatedRunningProcess(client, id, logPath, PollInterval, FollowInterval, StopLimit);
        SteamVrChecked?.Invoke(false, null);
        return process;
    }
}

internal sealed class ElevatedRunningProcess : IRunningProcess
{
    private const int MaxEarlyLines = 1000;

    private const int ExitUnknown = -1;

    private static readonly TimeSpan DisposeLimit = TimeSpan.FromSeconds(5);

    private readonly IHelperClient client;
    private readonly string logPath;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan followInterval;
    private readonly TimeSpan stopLimit;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task follow;

    private readonly object gate = new();
    private Action<string>? listeners;
    private List<string>? early = [];
    private bool disposed;
    private Task? stopSent;

    public ElevatedRunningProcess(
        IHelperClient client, int id, string logPath, TimeSpan pollInterval, TimeSpan followInterval, TimeSpan stopLimit)
    {
        this.client = client;
        this.logPath = logPath;
        this.pollInterval = pollInterval;
        this.followInterval = followInterval;
        this.stopLimit = stopLimit;
        Id = id;
        Exited = exited.Task;
        follow = Task.Run(() => FollowAsync(stop.Token));
        _ = Task.Run(() => PollExitAsync(stop.Token));
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

    public void Kill() => SendStop(clean: false);

    public async Task<bool> EndCleanlyAsync(TimeSpan limit)
    {
        if (SendStop(clean: true) is not { } sent)
        {
            return exited.Task.IsCompleted;
        }

        try
        {
            await sent.WaitAsync(stopLimit).ConfigureAwait(false);
            await exited.Task.WaitAsync(limit).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private Task? SendStop(bool clean)
    {
        lock (gate)
        {
            if (stopSent is not null || disposed)
            {
                return null;
            }

            stopSent = Task.Run(async () =>
            {
                try
                {
                    await client.SendAsync(new HelperRequest(HelperCommand.StopMonadoElevated, Clean: clean), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            });
            return stopSent;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? sent;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            sent = stopSent;
        }

        if (sent is not null)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await sent.WaitAsync(stopLimit).ConfigureAwait(false);
                var left = stopLimit - clock.Elapsed;
                if (left > TimeSpan.Zero)
                {
                    await exited.Task.WaitAsync(left).ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
            }
        }

        await stop.CancelAsync().ConfigureAwait(false);
        exited.TrySetResult(ExitUnknown);
        try
        {
            await follow.WaitAsync(DisposeLimit).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        stop.Dispose();
    }

    private async Task PollExitAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var reply = await client.SendAsync(HelperCommand.WaitMonadoElevated, ct).ConfigureAwait(false);
                if (reply.Value is { } value && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                {
                    exited.TrySetResult(code);
                    return;
                }

                if (reply.NotDelivered)
                {
                    exited.TrySetResult(ExitUnknown);
                    return;
                }

                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task FollowAsync(CancellationToken ct)
    {
        long position = 0;
        var pending = new StringBuilder();
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(logPath))
                    {
                        await using var file = new FileStream(
                            logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (file.Length < position)
                        {
                            position = 0;
                            pending.Clear();
                            decoder = Encoding.UTF8.GetDecoder();
                        }

                        file.Position = position;
                        var buffer = new byte[8192];
                        int read;
                        while ((read = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            position += read;
                            var chars = new char[decoder.GetCharCount(buffer, 0, read)];
                            var got = decoder.GetChars(buffer, 0, read, chars, 0);
                            for (var i = 0; i < got; i++)
                            {
                                if (chars[i] == '\n')
                                {
                                    Publish(pending.ToString().TrimEnd('\r'));
                                    pending.Clear();
                                }
                                else
                                {
                                    pending.Append(chars[i]);
                                }
                            }
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }

                await Task.Delay(followInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
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
