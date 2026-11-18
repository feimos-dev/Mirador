namespace Mirador.Core.OpenVr;

/// <summary>
/// Raises <see cref="Changed"/> once after files in any of a few folders were created, changed, renamed or deleted
/// (a burst of events, such as a file replaced through a temporary one, is one change once <c>debounce</c> passed
/// quietly). Each folder is watched with what is below it (OpenLamina's DLLs are in its <c>bin</c>), and its own deletion
/// is noticed through its parent. A folder that isn't there (yet, or any more) is waited for by watching the nearest
/// folder above it that exists, by itself only (never, say, the whole of <c>%LOCALAPPDATA%</c>); once the folder
/// appears it is watched and that is a change too. A folder that can't be watched is tried again a few times, each
/// after a longer pause, then left (logged once): the caller's own refreshes still catch up. <see cref="Changed"/> comes
/// on a pool thread; nothing here throws on a pool thread.
/// </summary>
public sealed class FolderWatch : IDisposable
{
    /// <summary>How often a folder that can't be watched is tried before it is left.</summary>
    internal const int MaxAttempts = 5;

    /// <summary>A watcher that ran this long without trouble starts its count of failures afresh.</summary>
    private static readonly TimeSpan HealthyAfter = TimeSpan.FromMinutes(1);

    private readonly object gate = new();
    private readonly TimeSpan debounce;
    private readonly Timer timer;
    private readonly List<Target> targets;
    private readonly Action<string>? log;
    private readonly Func<string, FileSystemWatcher> create;
    private readonly TimeSpan retryBase;
    private bool disposed;

    /// <param name="dirs">The folders: for Home's OpenVR card, <c>%LOCALAPPDATA%\openvr</c> and OpenLamina's install folder.</param>
    /// <param name="debounce">How long it must stay quiet after an event before <see cref="Changed"/>.</param>
    /// <param name="log">Where a folder that is left after repeated failures is said, once each; null: nowhere.</param>
    public FolderWatch(IReadOnlyList<string> dirs, TimeSpan debounce, Action<string>? log = null)
        : this(dirs, debounce, log, folder => new FileSystemWatcher(folder), TimeSpan.FromSeconds(1))
    {
    }

    /// <summary>For tests: <paramref name="create"/> makes each watcher, and the first retry waits <paramref name="retryBase"/> (each next one twice as long).</summary>
    internal FolderWatch(IReadOnlyList<string> dirs, TimeSpan debounce, Action<string>? log, Func<string, FileSystemWatcher> create, TimeSpan retryBase)
    {
        this.debounce = debounce;
        this.log = log;
        this.create = create;
        this.retryBase = retryBase;
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

    /// <summary>Something in a watched folder changed, or a folder appeared or went.</summary>
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
                target.Stop();
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

    private void Log(string text)
    {
        try
        {
            log?.Invoke(text);
        }
        catch (Exception)
        {
            // Logging can't stop the watch either.
        }
    }

    /// <summary>Runs <paramref name="work"/> on the pool under the lock, unless disposed; whatever it throws is logged, never left to end the process.</summary>
    private void Later(Action work) => ThreadPool.QueueUserWorkItem(_ =>
    {
        try
        {
            lock (gate)
            {
                if (!disposed)
                {
                    work();
                }
            }
        }
        catch (Exception e)
        {
            Log($"Watching a folder for changes failed: {e.Message}");
        }
    });

    /// <summary>The folder itself when it exists, else the nearest folder above it that does; null when none does.</summary>
    private static string? Nearest(string path)
    {
        try
        {
            for (var at = path; at is not null; at = Path.GetDirectoryName(at))
            {
                if (Directory.Exists(at))
                {
                    return at;
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>One folder: watched itself (and through its parent) when it exists, else through the nearest folder above it.</summary>
    private sealed class Target(FolderWatch owner, string path)
    {
        private const NotifyFilters Everything =
            NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;

        private FileSystemWatcher? watcher;
        private FileSystemWatcher? parent;
        private Timer? retry;
        private bool watchingPath;
        private int failures;
        private bool left;
        private long armedAt;

        /// <summary>
        /// Watches the folder, or the nearest one above it. A folder on the way that appears meanwhile is taken up at
        /// once (at most as many steps as the path has). A failure is tried again later (<see cref="Failed"/>). Called
        /// under the owner's lock.
        /// </summary>
        public void Arm(bool initial = false)
        {
            var wasWatchingPath = watchingPath;
            for (var steps = 0; steps < 64; steps++)
            {
                Disarm();
                if (Nearest(path) is not { } at)
                {
                    Failed($"none of the folders above {path} is there");
                    return;
                }

                var atPath = Same(at, path);
                try
                {
                    watcher = Watch(at, recursive: atPath, atPath ? Everything : NotifyFilters.DirectoryName, filter: null);
                }
                catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    // The folder went between the look and the watch, or can't be watched: look again later.
                    Failed(e.Message);
                    return;
                }

                watchingPath = atPath;
                if (atPath && Path.GetDirectoryName(path) is { } above)
                {
                    try
                    {
                        // The folder's own deletion or rename shows only in its parent.
                        parent = Watch(above, recursive: false, NotifyFilters.DirectoryName, Path.GetFileName(path));
                    }
                    catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        parent = null; // the folder is still watched; only its deletion may be noticed later
                    }
                }

                if (atPath || Same(Nearest(path), at))
                {
                    break;
                }
            }

            armedAt = Environment.TickCount64;
            if (!initial && watchingPath != wasWatchingPath)
            {
                owner.Bump(); // the folder appeared, or went
            }
        }

        /// <summary>Stops for good (the owner is disposed). Called under the owner's lock.</summary>
        public void Stop()
        {
            Disarm();
            retry?.Dispose();
            retry = null;
        }

        private FileSystemWatcher Watch(string folder, bool recursive, NotifyFilters notify, string? filter)
        {
            var made = owner.create(folder);
            try
            {
                made.IncludeSubdirectories = recursive;
                made.NotifyFilter = notify;
                if (filter is not null)
                {
                    made.Filter = filter;
                }

                made.Created += OnEvent;
                made.Changed += OnEvent;
                made.Deleted += OnEvent;
                made.Renamed += OnEvent;
                made.Error += OnError;
                made.EnableRaisingEvents = true;
                return made;
            }
            catch
            {
                made.Dispose();
                throw;
            }
        }

        private void Disarm()
        {
            foreach (var old in new[] { watcher, parent })
            {
                if (old is not null)
                {
                    old.EnableRaisingEvents = false;
                    old.Dispose();
                }
            }

            watcher = null;
            parent = null;
        }

        /// <summary>
        /// Watching failed: tried again after 1, 2, 4… times the first pause, at most <see cref="MaxAttempts"/> times in
        /// all; then the folder is left, and said once. Called under the owner's lock.
        /// </summary>
        private void Failed(string why)
        {
            Disarm();
            if (left)
            {
                return;
            }

            if (++failures >= MaxAttempts)
            {
                left = true;
                owner.Log($"Mirador stopped watching {path} for changes after {failures} tries ({why}).");
                return;
            }

            var wait = owner.retryBase * Math.Pow(2, failures - 1);
            retry?.Dispose();
            retry = new Timer(_ => owner.Later(() =>
            {
                retry?.Dispose();
                retry = null;
                Arm();
            }), null, wait, Timeout.InfiniteTimeSpan);
        }

        private void OnEvent(object sender, FileSystemEventArgs e)
        {
            lock (owner.gate)
            {
                if (owner.disposed)
                {
                    return;
                }

                if (ReferenceEquals(sender, watcher) && watchingPath)
                {
                    owner.Bump();

                    // Its own contents deleted with it: when the folder itself is gone, wait for it above.
                    if (e.ChangeType == WatcherChangeTypes.Deleted && !Directory.Exists(path))
                    {
                        owner.Later(Rearm);
                    }

                    return;
                }

                if (!ReferenceEquals(sender, watcher) && !ReferenceEquals(sender, parent))
                {
                    return;
                }
            }

            // The folder itself went or came (its parent's event), or a folder above it changed: look again, off this
            // watcher's own thread.
            owner.Later(Rearm);
        }

        private void OnError(object sender, ErrorEventArgs e) => owner.Later(() =>
        {
            if (!ReferenceEquals(sender, watcher) && !ReferenceEquals(sender, parent))
            {
                return;
            }

            // Events may have been lost (a full buffer, the folder gone): say something changed, and watch anew after a
            // pause. A watcher that keeps failing is left after a few tries.
            if (Environment.TickCount64 - armedAt > (long)HealthyAfter.TotalMilliseconds)
            {
                failures = 0;
            }

            owner.Bump();
            Failed(e.GetException().Message);
        });

        /// <summary>Watches anew when the folder, or one on its way, appeared or went. Called under the owner's lock.</summary>
        private void Rearm()
        {
            if (left || retry is not null && watcher is null)
            {
                return; // a retry is due, or the folder was left
            }

            var at = Nearest(path);
            if (watcher is not null && Same(at, watcher.Path) && (Same(at, path) == watchingPath))
            {
                return; // nothing on the way changed
            }

            Arm();
        }
    }
}
