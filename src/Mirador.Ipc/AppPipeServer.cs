using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Mirador.Ipc;

public sealed class AppPipeServer(string name, Func<string, int?, CancellationToken, Task<string>> handle) : IAsyncDisposable
{
    public AppPipeServer(string name, Func<string, CancellationToken, Task<string>> handle)
        : this(name, (line, _, ct) => handle(line, ct))
    {
    }

    public const int MaxServed = 16;

    private const int BufferBytes = 2 * (AppPipe.MaxLineBytes + 1);

    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim slots = new(MaxServed);
    private readonly List<Task> serving = [];
    private NamedPipeServerStream? first;
    private Task? loop;

    internal TimeSpan ReadLimit { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public event Action<Exception>? Failed;

    public event Action<Exception>? HandlerFailed;

    public void Start()
    {
        first = CreatePipe(firstInstance: true);
        loop = Task.Run(() => ListenAsync(first, stopping.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            await loop.ConfigureAwait(false);
        }

        Task[] left;
        lock (serving)
        {
            left = [.. serving];
        }

        await Task.WhenAll(left).ConfigureAwait(false);
        stopping.Dispose();
    }

    internal static PipeSecurity CreatePipeSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return OperatingSystem.IsWindows()
            ? NamedPipeServerStreamAcl.Create(
                name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                options, BufferBytes, BufferBytes, CreatePipeSecurity())
            : new NamedPipeServerStream(
                name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, BufferBytes, BufferBytes);
    }

    private async Task<NamedPipeServerStream> NextPipeAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return CreatePipe(firstInstance: false);
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ListenAsync(NamedPipeServerStream firstListener, CancellationToken ct)
    {
        NamedPipeServerStream? listening = firstListener;
        try
        {
            while (true)
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await listening!.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    slots.Release();
                    var dead = listening;
                    listening = null;
                    try
                    {
                        listening = await NextPipeAsync(ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        await dead.DisposeAsync().ConfigureAwait(false);
                    }

                    continue;
                }
                catch
                {
                    slots.Release();
                    throw;
                }

                var connected = listening;
                listening = null;
                try
                {
                    listening = await NextPipeAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    Track(ServeAsync(connected, ct));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                Failed?.Invoke(e);
            }
            catch (Exception)
            {
            }
        }
        finally
        {
            if (listening is not null)
            {
                await listening.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void Track(Task task)
    {
        lock (serving)
        {
            serving.Add(task);
        }

        _ = task.ContinueWith(
            t =>
            {
                lock (serving)
                {
                    serving.Remove(t);
                }
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            string? request;
            using (var read = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                read.CancelAfter(ReadLimit);
                request = await ReadLineAsync(pipe, read.Token).ConfigureAwait(false);
            }

            if (request is null)
            {
                return;
            }

            var reply = AppPipe.OneLine(await AnswerAsync(request, CallerProcessId(pipe), ct).ConfigureAwait(false));
            await pipe.WriteAsync(Encoding.ASCII.GetBytes(reply + "\n"), ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
            await WaitForHangUpAsync(pipe, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                if (pipe.IsConnected)
                {
                    pipe.Disconnect();
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }

            await pipe.DisposeAsync().ConfigureAwait(false);
            slots.Release();
        }
    }

    private async Task<string> AnswerAsync(string request, int? caller, CancellationToken ct)
    {
        try
        {
            return await handle(request, caller, ct).ConfigureAwait(false) ?? AppPipe.FailPrefix + "Mirador had no answer.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            try
            {
                HandlerFailed?.Invoke(e);
            }
            catch (Exception)
            {
            }

            return AppPipe.FailPrefix + "Mirador failed: " + e.Message;
        }
    }

    private async Task WaitForHangUpAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ReadLimit);
        var buffer = new byte[64];
        try
        {
            while (await pipe.ReadAsync(buffer, limit.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }

    internal static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var line = new byte[AppPipe.MaxLineBytes];
        var one = new byte[1];
        var count = 0;
        while (true)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0)
            {
                return null;
            }

            if (one[0] == (byte)'\n')
            {
                break;
            }

            if (count == AppPipe.MaxLineBytes)
            {
                return null;
            }

            line[count++] = one[0];
        }

        if (count > 0 && line[count - 1] == (byte)'\r')
        {
            count--;
        }

        return Encoding.UTF8.GetString(line, 0, count);
    }

    private static int? CallerProcessId(NamedPipeServerStream pipe)
    {
        try
        {
            return GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id) ? unchecked((int)id) : null;
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
}
