using Mirador.Ipc;

namespace Mirador.Core.Xr;

/// <summary>
/// The pipe the home and the dashboard reach their <see cref="XrHost"/> on: listens for as long as Mirador runs, and
/// takes only the processes the host itself started.
/// </summary>
public sealed class XrHostPipe(XrHost host, string name, Action<string> log) : IAsyncDisposable
{
    private XrLinkServer? server;

    /// <summary>Begins listening; a pipe that can't be made is logged, and the programs then run without Mirador.</summary>
    public void Start()
    {
        var listening = new XrLinkServer(name, host.Started);
        listening.Connected += OnConnected;
        listening.Failed += e => log($"The pipe to the home and the dashboard stopped: {e.Message}");
        try
        {
            listening.Start();
            server = listening;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"The pipe to the home and the dashboard couldn't be made: {e.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (server is { } listening)
        {
            server = null;
            await listening.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnConnected(XrLinkConnection connection)
    {
        var program = new Program(connection);
        connection.Line += line => host.Line(connection.Role, line);
        connection.Closed += () => host.Disconnected(connection.Role, program);
        host.Connected(connection.Role, program);
    }

    private sealed class Program(XrLinkConnection connection) : IXrProgram
    {
        public void Send(string line) => connection.Send(line);
    }
}
