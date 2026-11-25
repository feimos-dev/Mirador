using System.Collections.Concurrent;

namespace Mirador.Core.Monado;

public sealed class FileHashCache(Func<string, string> hashOf)
{
    private readonly ConcurrentDictionary<string, (long Length, DateTime Written, string Hash)> known = new(StringComparer.OrdinalIgnoreCase);

    public string HashOf(string path)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        var (length, written) = (info.Length, info.LastWriteTimeUtc);
        if (known.TryGetValue(full, out var kept) && kept.Length == length && kept.Written == written)
        {
            return kept.Hash;
        }

        var hash = hashOf(full);
        known[full] = (length, written, hash);
        return hash;
    }
}
