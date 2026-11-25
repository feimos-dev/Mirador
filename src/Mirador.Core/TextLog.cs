using System.Text;

namespace Mirador.Core;

public sealed class TextLog(string path, long maxBytes = 5_000_000) : IDisposable
{
    private const int TailLines = 200;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object gate = new();
    private readonly Queue<string> tail = new();
    private FileStream? file;
    private long size;
    private long rollAgainAt;
    private bool disposed;

    public IReadOnlyList<string> Tail
    {
        get
        {
            lock (gate)
            {
                return tail.ToArray();
            }
        }
    }

    public void Write(string line)
    {
        var bytes = Utf8.GetBytes(line + Environment.NewLine);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            tail.Enqueue(line);
            if (tail.Count > TailLines)
            {
                tail.Dequeue();
            }

            Open();
            if (size > 0 && size + bytes.Length > maxBytes && size >= rollAgainAt)
            {
                Roll();
            }

            file!.Write(bytes);
            file.Flush();
            size += bytes.Length;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            file?.Dispose();
            file = null;
        }
    }

    private void Open()
    {
        if (file is not null)
        {
            return;
        }

        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        size = file.Length;
    }

    private void Roll()
    {
        file!.Dispose();
        file = null;
        try
        {
            File.Move(path, path + ".1", overwrite: true);
            rollAgainAt = 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            rollAgainAt = size + Math.Max(1, maxBytes / 10);
        }

        Open();
    }
}
