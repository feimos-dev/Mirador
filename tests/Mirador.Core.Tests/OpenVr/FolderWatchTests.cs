using Mirador.Core.OpenVr;

namespace Mirador.Core.Tests.OpenVr;

public sealed class FolderWatchTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan EventLimit = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);

    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    /// <summary>Counts <see cref="FolderWatch.Changed"/>, and completes <see cref="Next"/> at each one.</summary>
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

        // As OpenLamina's installer and SteamVR write it: a temp file, moved over the old one.
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
        await counter.Next.WaitAsync(EventLimit); // the folder appearing is a change too
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
