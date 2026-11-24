using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;

namespace Mirador.Ipc;

/// <summary>
/// One end of an <see cref="XrLink"/> connection: lines come in on <see cref="Line"/>, <see cref="Send"/> queues a
/// line to go out. Neither blocks the caller, and nothing here throws once the connection is made: a broken pipe ends
/// it and raises <see cref="Closed"/>.
/// </summary>
public sealed class XrLinkConnection : IDisposable
{
    private readonly Stream stream;
    private readonly Channel<string> outgoing = Channel.CreateBounded<string>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource ending = new();
    private int closed;
    private int started;

    internal XrLinkConnection(Stream stream, string role, int? processId)
    {
        this.stream = stream;
        Role = role;
        ProcessId = processId;
    }

    /// <summary>The program at the other end, as it said in its first line: <see cref="XrLink.HomeRole"/> or <see cref="XrLink.DashboardRole"/>.</summary>
    public string Role { get; }

    /// <summary>The process at the other end, where Windows tells; null on the program's own end.</summary>
    public int? ProcessId { get; }

    /// <summary>Whether the connection still stands.</summary>
    public bool IsOpen => Volatile.Read(ref closed) == 0;

    /// <summary>Raised on a background thread with each line that comes in, in order; a handler that throws is ignored.</summary>
    public event Action<string>? Line;

    /// <summary>Raised once, on a background thread, when the connection has ended, whoever ended it.</summary>
    public event Action? Closed;

    /// <summary>
    /// Connects to the Mirador listening on <paramref name="name"/> as <paramref name="role"/>; null when no Mirador
    /// of this user answers within <paramref name="timeout"/>. Call <see cref="Start"/> once the handlers are added.
    /// </summary>
    public static XrLinkConnection? Connect(string name, string role, TimeSpan timeout)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            pipe.Connect(timeout);
            if (!AppPipeClient.OwnedByThisUser(pipe))
            {
                pipe.Dispose();
                return null;
            }

            pipe.Write(Encoding.ASCII.GetBytes($"{XrLink.Hello} {role}\n"));
            pipe.Flush();
            return new XrLinkConnection(pipe, role, null);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            pipe?.Dispose();
            return null;
        }
    }

    /// <summary>Begins reading and writing. Call it once, after adding the handlers, so that no line is missed.</summary>
    public void Start() => _ = Task.Run(RunAsync);

    /// <summary>Whether <see cref="RunAsync"/> has the stream; before that, ending the connection closes it.</summary>
    private bool Claim() => Interlocked.Exchange(ref started, 1) == 0;

    /// <summary>Queues <paramref name="line"/> to go out; dropped when the connection has ended.</summary>
    public void Send(string line)
    {
        if (IsOpen)
        {
            outgoing.Writer.TryWrite(AppPipe.OneLine(line));
        }
    }

    /// <summary>Ends the connection. Lines queued but not yet written are lost.</summary>
    public void Dispose()
    {
        End();
        if (Claim())
        {
            stream.Dispose();
        }
    }

    /// <summary>Serves the connection until it ends; never throws.</summary>
    internal async Task RunAsync()
    {
        if (!Claim())
        {
            return;
        }

        var writing = WriteAsync();
        try
        {
            while (await AppPipeServer.ReadLineAsync(stream, ending.Token).ConfigureAwait(false) is { } line)
            {
                try
                {
                    Line?.Invoke(line);
                }
                catch (Exception)
                {
                    // A listener's mistake doesn't end the connection.
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }

        End();
        await writing.ConfigureAwait(false);
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }

        ending.Dispose();
        try
        {
            Closed?.Invoke();
        }
        catch (Exception)
        {
        }
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var line in outgoing.Reader.ReadAllAsync(ending.Token).ConfigureAwait(false))
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), ending.Token).ConfigureAwait(false);
                await stream.FlushAsync(ending.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }

        End();
    }

    private void End()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
        {
            return;
        }

        outgoing.Writer.TryComplete();
        try
        {
            ending.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
