using System.Collections.Concurrent;
using Mirador.Ipc;

namespace Mirador.Xr;

internal sealed class Link : IDisposable
{
    private static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(5);

    private readonly XrLinkConnection? connection;
    private readonly ConcurrentQueue<string> lines = new();

    private Link(XrLinkConnection? connection)
    {
        this.connection = connection;
        if (connection is not null)
        {
            connection.Line += lines.Enqueue;
            connection.Start();
        }
    }

    public static Link Alone() => new(null);

    public static Link? Connect(string role) =>
        XrLinkConnection.Connect(XrLink.CurrentName(), role, ConnectLimit) is { } connection ? new Link(connection) : null;

    public bool IsAlone => connection is null;

    public bool Lost => connection is { IsOpen: false };

    public void Send(string line) => connection?.Send(line);

    public bool TryRead(out string line) => lines.TryDequeue(out line!);

    public void Dispose() => connection?.Dispose();
}
