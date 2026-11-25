using Mirador.Core.OpenVr;

namespace Mirador.Core.Tests.OpenVr;

public sealed class FolderWatchTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan EventLimit = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);

    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private sealed class Counter
    {
        private int count;
        private TaskCompletionSource next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Counter(FolderWatch watch) => watch.Changed += () =>
        {
            Interlocked.Increment(ref count);
            Volatile.Read(ref next).TrySetResult();
        };

        public int Count => Volatile.Read(ref count);

        public Task Next => Volatile.Read(ref next).Task;

        public void Reset()
        {
            Interlocked.Exchange(ref count, 0);
            Interlocked.Exchange(ref next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

    [Fact]
    public async Task Replacing_a_file_through_a_temp_file_raises_changed_once()
    {
        var openvr = dir.File("openvr");
        Directory.CreateDirectory(openvr);
        var file = Path.Combine(openvr, "openvrpaths.vrpath");
        File.WriteAllText(file, "{}");
        using var watch = new FolderWatch([openvr], Debounce);
        var counter = new Counter(watch);

        var temp = Path.Combine(openvr, "x.tmp");
        File.WriteAllText(temp, """{"runtime": ["C:\\OpenLamina"]}""");
        File.Move(temp, file, overwrite: true);

        await counter.Next.WaitAsync(EventLimit);
        await Task.Delay(Quiet);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task A_folder_created_later_is_watched()
    {
        var target = dir.File("later", "openvr");
        using var watch = new FolderWatch([target], Debounce);
        var counter = new Counter(watch);

        Directory.CreateDirectory(target);
        await counter.Next.WaitAsync(EventLimit);
        await Task.Delay(Quiet);
        counter.Reset();

        File.WriteAllText(Path.Combine(target, "openvrpaths.vrpath"), "{}");

        await counter.Next.WaitAsync(EventLimit);
    }

    [Fact]
    public async Task A_change_beside_the_watched_folder_is_not_one()
    {
        var target = dir.File("openvr");
        Directory.CreateDirectory(target);
        using var watch = new FolderWatch([target], Debounce);
        var counter = new Counter(watch);

        File.WriteAllText(dir.File("other.txt"), "x");
        Directory.CreateDirectory(dir.File("elsewhere", "deep"));
        File.WriteAllText(dir.File("elsewhere", "deep", "x.txt"), "x");
        await Task.Delay(Quiet);

        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task A_change_below_the_watched_folder_is_one()
    {
        var openLamina = dir.File("OpenLamina");
        Directory.CreateDirectory(Path.Combine(openLamina, "bin"));
        using var watch = new FolderWatch([openLamina], Debounce);
        var counter = new Counter(watch);

        File.WriteAllText(Path.Combine(openLamina, "bin", "vrclient_x64.dll"), "new");

        await counter.Next.WaitAsync(EventLimit);
    }

    [Fact]
    public async Task A_folder_deleted_and_made_again_is_watched_again()
    {
        var openLamina = dir.File("OpenLamina");
        Directory.CreateDirectory(Path.Combine(openLamina, "bin"));
        File.WriteAllText(Path.Combine(openLamina, "bin", "vrclient_x64.dll"), "old");
        using var watch = new FolderWatch([openLamina], Debounce);
        var counter = new Counter(watch);

        Directory.Delete(openLamina, recursive: true);
        await counter.Next.WaitAsync(EventLimit);
        await Task.Delay(Quiet);
        counter.Reset();

        Directory.CreateDirectory(Path.Combine(openLamina, "bin"));
        await counter.Next.WaitAsync(EventLimit);
        await Task.Delay(Quiet);
        counter.Reset();
        File.WriteAllText(Path.Combine(openLamina, "bin", "vrclient_x64.dll"), "new");
        await counter.Next.WaitAsync(EventLimit);
    }

    [Fact]
    public async Task A_folder_gone_before_its_watcher_is_made_is_waited_for()
    {
        var openvr = dir.File("openvr");
        Directory.CreateDirectory(openvr);
        var gone = false;

        using var watch = new FolderWatch([openvr], Debounce, log: null, folder =>
        {
            if (!gone && string.Equals(folder, openvr, StringComparison.OrdinalIgnoreCase))
            {
                gone = true;
                Directory.Delete(openvr);
            }

            return new FileSystemWatcher(folder);
        }, retryBase: TimeSpan.FromMilliseconds(50));
        var counter = new Counter(watch);

        Directory.CreateDirectory(openvr);
        await counter.Next.WaitAsync(EventLimit);
        await Task.Delay(Quiet);
        counter.Reset();
        File.WriteAllText(Path.Combine(openvr, "openvrpaths.vrpath"), "{}");
        await counter.Next.WaitAsync(EventLimit);
    }

    [Fact]
    public async Task A_folder_that_never_can_be_watched_is_left_after_a_few_tries_and_said_once()
    {
        var openvr = dir.File("openvr");
        Directory.CreateDirectory(openvr);
        var tries = 0;
        var said = new List<string>();
        using var watch = new FolderWatch([openvr], Debounce, text => { lock (said) { said.Add(text); } }, _ =>
        {
            Interlocked.Increment(ref tries);
            throw new UnauthorizedAccessException("Access is denied.");
        }, retryBase: TimeSpan.FromMilliseconds(10));

        var deadline = DateTime.UtcNow + EventLimit;
        while (Volatile.Read(ref tries) < FolderWatch.MaxAttempts && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await Task.Delay(Quiet);
        Assert.Equal(FolderWatch.MaxAttempts, Volatile.Read(ref tries));
        lock (said)
        {
            Assert.Single(said);
            Assert.Contains("stopped watching", said[0]);
        }
    }

    [Fact]
    public async Task Dispose_stops_events()
    {
        var openvr = dir.File("openvr");
        Directory.CreateDirectory(openvr);
        var watch = new FolderWatch([openvr], Debounce);
        var counter = new Counter(watch);

        watch.Dispose();
        File.WriteAllText(Path.Combine(openvr, "openvrpaths.vrpath"), "{}");
        await Task.Delay(Quiet);

        Assert.Equal(0, counter.Count);
    }
}
