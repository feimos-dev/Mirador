using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;

namespace Mirador.Ipc;

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

    public string Role { get; }

    public int? ProcessId { get; }

    public bool IsOpen => Volatile.Read(ref closed) == 0;

    public event Action<string>? Line;

    public event Action? Closed;

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

    public void Start() => _ = Task.Run(RunAsync);

    private bool Claim() => Interlocked.Exchange(ref started, 1) == 0;

    public void Send(string line)
    {
        if (IsOpen)
        {
            outgoing.Writer.TryWrite(AppPipe.OneLine(line));
        }
    }

    public void Dispose()
    {
        End();
        if (Claim())
        {
            stream.Dispose();
        }
    }

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
