using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Mirador.Ipc;

public sealed class XrLinkServer(string name, Func<int?, string, bool>? allow = null) : IAsyncDisposable
{
    public const int MaxServed = 4;

    private const int BufferBytes = 2 * (AppPipe.MaxLineBytes + 1);

    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim slots = new(MaxServed);
    private readonly List<Task> serving = [];
    private Task? loop;

    internal TimeSpan HelloLimit { get; init; } = TimeSpan.FromSeconds(5);

    public event Action<XrLinkConnection>? Connected;

    public event Action<Exception>? Failed;

    public void Start()
    {
        var first = CreatePipe(firstInstance: true);
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

    private NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return OperatingSystem.IsWindows()
            ? NamedPipeServerStreamAcl.Create(
                name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                options, BufferBytes, BufferBytes, AppPipeServer.CreatePipeSecurity())
            : new NamedPipeServerStream(
                name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, BufferBytes, BufferBytes);
    }

    private async Task ListenAsync(NamedPipeServerStream first, CancellationToken ct)
    {
        NamedPipeServerStream? listening = first;
        try
        {
            while (true)
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
                var connected = listening ?? CreatePipe(firstInstance: false);
                listening = null;
                try
                {
                    await connected.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    slots.Release();
                    await connected.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                catch
                {
                    slots.Release();
                    await connected.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                Track(ServeAsync(connected, ct));
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
        XrLinkConnection? connection = null;
        try
        {
            string? hello;
            using (var read = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                read.CancelAfter(HelloLimit);
                hello = await AppPipeServer.ReadLineAsync(pipe, read.Token).ConfigureAwait(false);
            }

            var (word, role) = XrLink.Split(hello ?? "");
            var caller = CallerProcessId(pipe);
            if (word != XrLink.Hello || role is not (XrLink.HomeRole or XrLink.DashboardRole) || allow?.Invoke(caller, role) == false)
            {
                return;
            }

            connection = new XrLinkConnection(pipe, role, caller);
            Connected?.Invoke(connection);
            await using (ct.Register(connection.Dispose))
            {
                await connection.RunAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            connection?.Dispose();
            await pipe.DisposeAsync().ConfigureAwait(false);
            slots.Release();
        }
    }

    private static int? CallerProcessId(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

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
