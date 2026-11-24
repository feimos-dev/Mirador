using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Mirador.Ipc;

/// <summary>
/// Mirador's end of the <see cref="XrLink"/> pipe: takes the connections of the home and the dashboard and hands each
/// to <see cref="Connected"/> once it has said which it is. Only this user's processes can open the pipe, and
/// <paramref name="allow"/> can narrow that to the processes Mirador started.
/// </summary>
/// <param name="name">The pipe's name (<see cref="XrLink.CurrentName"/>).</param>
/// <param name="allow">Whether the process with this id may connect as this role; null: any of this user's may.</param>
public sealed class XrLinkServer(string name, Func<int?, string, bool>? allow = null) : IAsyncDisposable
{
    /// <summary>How many programs are served at once; a further one waits.</summary>
    public const int MaxServed = 4;

    private const int BufferBytes = 2 * (AppPipe.MaxLineBytes + 1);

    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim slots = new(MaxServed);
    private readonly List<Task> serving = [];
    private Task? loop;

    /// <summary>How long a program has to send its first line.</summary>
    internal TimeSpan HelloLimit { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Raised on a background thread for each program that connected and said which it is. The handler adds its own
    /// handlers to the connection and returns; lines are read only after that. A handler that throws loses the connection.
    /// </summary>
    public event Action<XrLinkConnection>? Connected;

    /// <summary>Raised when the pipe can't be listened on any more.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Begins listening.</summary>
    /// <exception cref="IOException">Another process holds the pipe's name.</exception>
    /// <exception cref="UnauthorizedAccessException">Another process holds the pipe's name.</exception>
    public void Start()
    {
        var first = CreatePipe(firstInstance: true);
        loop = Task.Run(() => ListenAsync(first, stopping.Token));
    }

    /// <summary>Stops listening and ends every connection.</summary>
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
            // Elsewhere only for the tests: .NET's pipes there have no permissions or first instances.
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
                    // The caller went away before it was accepted.
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
            // A Connected handler threw: the program is dropped and connects again when it is started again.
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
