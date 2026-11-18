namespace Mirador.Core.OpenVr;

/// <summary>
/// Raises <see cref="Changed"/> once after files in any of a few folders were created, changed, renamed or deleted
/// (a burst of events, such as a file replaced through a temporary one, is one change once <c>debounce</c> passed
/// quietly). Each folder is watched with what is below it (OpenLamina's DLLs are in its <c>bin</c>). A folder that isn't
/// there yet is waited for by watching the nearest folder above it that exists, by itself only (never, say, the whole
/// of <c>%LOCALAPPDATA%</c>); once the folder appears it is watched and that is a change too. <see cref="Changed"/>
/// comes on a pool thread.
/// </summary>
public sealed class FolderWatch : IDisposable
{
    private readonly object gate = new();
    private readonly TimeSpan debounce;
    private readonly Timer timer;
    private readonly List<Target> targets;
    private bool disposed;

    /// <param name="dirs">The folders: for Home's OpenVR card, <c>%LOCALAPPDATA%\openvr</c> and OpenLamina's install folder.</param>
    /// <param name="debounce">How long it must stay quiet after an event before <see cref="Changed"/>.</param>
    public FolderWatch(IReadOnlyList<string> dirs, TimeSpan debounce)
    {
        this.debounce = debounce;
        timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        targets = [.. dirs.Select(d => new Target(this, Path.TrimEndingDirectorySeparator(Path.GetFullPath(d))))];
        lock (gate)
        {
            foreach (var target in targets)
            {
                target.Arm(initial: true);
            }
        }
    }

    /// <summary>Something in a watched folder changed, or a folder waited for appeared.</summary>
    public event Action? Changed;

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var target in targets)
            {
                target.Disarm();
            }

            timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        timer.Dispose();
    }

    /// <summary>(Re)starts the quiet period. Called under the lock.</summary>
    private void Bump()
    {
        if (!disposed)
        {
            timer.Change(debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // A listener's failure doesn't end the watching.
        }
    }

    /// <summary>The folder itself when it exists, else the nearest folder above it that does; null when none does.</summary>
    private static string? Nearest(string path)
    {
        for (var at = path; at is not null; at = Path.GetDirectoryName(at))
        {
            if (Directory.Exists(at))
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>One folder: watched itself when it exists, else through the nearest folder above it.</summary>
    private sealed class Target(FolderWatch owner, string path)
    {
        private const NotifyFilters Everything =
            NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;

        private FileSystemWatcher? watcher;
        private bool watchingPath;

        /// <summary>
        /// Watches the folder, or the nearest one above it. A folder on the way that appears meanwhile is taken up at
        /// once (at most as many steps as the path has). Called under the owner's lock.
        /// </summary>
        public void Arm(bool initial = false)
        {
            var wasWatchingPath = watchingPath;
            for (var steps = 0; steps < 64; steps++)
            {
                Disarm();
                if (Nearest(path) is not { } at)
                {
                    return;
                }

                watchingPath = string.Equals(at, path, StringComparison.OrdinalIgnoreCase);
                var next = new FileSystemWatcher(at)
                {
                    IncludeSubdirectories = watchingPath,
                    NotifyFilter = watchingPath ? Everything : NotifyFilters.DirectoryName,
                };
                next.Created += OnEvent;
                next.Changed += OnEvent;
                next.Deleted += OnEvent;
                next.Renamed += OnEvent;
                next.Error += OnError;
                try
                {
                    next.EnableRaisingEvents = true;
                }
                catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    next.Dispose();
                    continue; // gone again meanwhile: look anew
                }

                watcher = next;
                if (watchingPath || string.Equals(Nearest(path), at, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            if (!initial && watchingPath && !wasWatchingPath)
            {
                owner.Bump(); // the folder appeared
            }
        }

        public void Disarm()
        {
            if (watcher is { } old)
            {
                watcher = null;
                old.EnableRaisingEvents = false;
                old.Dispose();
            }
        }

        private void OnEvent(object sender, FileSystemEventArgs e)
        {
            lock (owner.gate)
            {
                if (owner.disposed || !ReferenceEquals(sender, watcher))
                {
                    return;
                }

                if (watchingPath)
                {
                    owner.Bump();
                    return;
                }
            }

            // A folder above it changed: when one on the way appeared, move down, off this watcher's own thread.
            ThreadPool.QueueUserWorkItem(_ => Rearm(sender));
        }

        private void OnError(object sender, ErrorEventArgs e) => ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (owner.gate)
            {
                if (owner.disposed || !ReferenceEquals(sender, watcher))
                {
                    return;
                }

                // Events may have been lost (a full buffer, the folder gone): watch anew, and say something changed.
                watchingPath = false;
                Arm();
                owner.Bump();
            }
        });

        private void Rearm(object sender)
        {
            lock (owner.gate)
            {
                if (owner.disposed || !ReferenceEquals(sender, watcher) || watcher is null
                    || string.Equals(Nearest(path), watcher.Path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Arm();
            }
        }
    }
}
