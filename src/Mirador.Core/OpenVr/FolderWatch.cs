namespace Mirador.Core.OpenVr;

public sealed class FolderWatch : IDisposable
{
    internal const int MaxAttempts = 5;

    private static readonly TimeSpan HealthyAfter = TimeSpan.FromMinutes(1);

    private readonly object gate = new();
    private readonly TimeSpan debounce;
    private readonly Timer timer;
    private readonly List<Target> targets;
    private readonly Action<string>? log;
    private readonly Func<string, FileSystemWatcher> create;
    private readonly TimeSpan retryBase;
    private bool disposed;

    public FolderWatch(IReadOnlyList<string> dirs, TimeSpan debounce, Action<string>? log = null)
        : this(dirs, debounce, log, folder => new FileSystemWatcher(folder), TimeSpan.FromSeconds(1))
    {
    }

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
        }
    }

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
                    Failed(e.Message);
                    return;
                }

                watchingPath = atPath;
                if (atPath && Path.GetDirectoryName(path) is { } above)
                {
                    try
                    {
                        parent = Watch(above, recursive: false, NotifyFilters.DirectoryName, Path.GetFileName(path));
                    }
                    catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        parent = null;
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
                owner.Bump();
            }
        }

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

            owner.Later(Rearm);
        }

        private void OnError(object sender, ErrorEventArgs e) => owner.Later(() =>
        {
            if (!ReferenceEquals(sender, watcher) && !ReferenceEquals(sender, parent))
            {
                return;
            }

            if (Environment.TickCount64 - armedAt > (long)HealthyAfter.TotalMilliseconds)
            {
                failures = 0;
            }

            owner.Bump();
            Failed(e.GetException().Message);
        });

        private void Rearm()
        {
            if (left || retry is not null && watcher is null)
            {
                return;
            }

            var at = Nearest(path);
            if (watcher is not null && Same(at, watcher.Path) && (Same(at, path) == watchingPath))
            {
                return;
            }

            Arm();
        }
    }
}
