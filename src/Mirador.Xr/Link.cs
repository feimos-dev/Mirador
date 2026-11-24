using System.Collections.Concurrent;
using Mirador.Ipc;

namespace Mirador.Xr;

/// <summary>
/// A program's connection to the Mirador that started it. Lines from Mirador wait in a queue for the frame loop;
/// <see cref="Send"/> never blocks it.
/// </summary>
internal sealed class Link : IDisposable
{
    /// <summary>How long a program waits for Mirador's pipe when it starts.</summary>
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

    /// <summary>A link that goes nowhere, for trying a program without Mirador (<c>--alone</c>).</summary>
    public static Link Alone() => new(null);

    /// <summary>Connects as <paramref name="role"/>; null when no Mirador of this user listens.</summary>
    public static Link? Connect(string role) =>
        XrLinkConnection.Connect(XrLink.CurrentName(), role, ConnectLimit) is { } connection ? new Link(connection) : null;

    /// <summary>Whether no Mirador is at the other end, by choice.</summary>
    public bool IsAlone => connection is null;

    /// <summary>Whether Mirador has gone: the program has nothing left to do.</summary>
    public bool Lost => connection is { IsOpen: false };

    public void Send(string line) => connection?.Send(line);

    /// <summary>The next line from Mirador, when one waits.</summary>
    public bool TryRead(out string line) => lines.TryDequeue(out line!);

    public void Dispose() => connection?.Dispose();
}
