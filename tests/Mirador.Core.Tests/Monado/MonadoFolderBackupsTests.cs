using System.IO.Compression;
using Microsoft.Extensions.Time.Testing;
using Mirador.Core.Monado;

namespace Mirador.Core.Tests.Monado;

public sealed class MonadoFolderBackupsTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly FakeTimeProvider time = NewClock(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
    private bool running;

    private string MonadoDir => dir.File("monado");

    private string BackupsDir => dir.File("Mirador", "backups");

    private MonadoFolderBackups Backups(long maxBytes = 200_000_000) => new(MonadoDir, BackupsDir, time, () => running, maxBytes: maxBytes);

    /// <summary>A clock at <paramref name="utc"/> in a time zone five hours west of UTC, so that local and UTC names differ.</summary>
    private static FakeTimeProvider NewClock(DateTimeOffset utc)
    {
        var clock = new FakeTimeProvider(utc);
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("West", TimeSpan.FromHours(-5), "West", "West"));
        return clock;
    }

    public void Dispose() => dir.Dispose();

    private void Write(string relative, string text)
    {
        var path = Path.Combine(MonadoDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>What a folder holds: each file's path below it and its text.</summary>
    private static Dictionary<string, string> Contents(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f), File.ReadAllText);

    private void WriteFirstState()
    {
        Write("config_v0.json", "{ \"active\": \"tracking\" }");
        Write(Path.Combine("driver", "calibration_1.bin"), "first");
    }

    [Fact]
    public void A_first_snapshot_copies_the_whole_folder()
    {
        WriteFirstState();
        var backups = Backups();

        var backup = backups.SnapshotIfChanged(out var problem);

        Assert.Null(problem);
        Assert.NotNull(backup);
        Assert.Equal(Path.Combine(BackupsDir, "monado", "20261104-120000"), backup.Dir); // named in UTC
        Assert.Equal(time.GetUtcNow(), backup.Taken);
        Assert.True(File.Exists(Path.Combine(backup.Dir, "manifest.json")));
        Assert.Equal([backup], backups.Backups());

        // Restoring it after the folder went brings back exactly what was there.
        Directory.Delete(MonadoDir, recursive: true);
        Directory.CreateDirectory(MonadoDir);
        backups.Restore(backup);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["config_v0.json"] = "{ \"active\": \"tracking\" }",
                [Path.Combine("driver", "calibration_1.bin")] = "first",
            },
            Contents(MonadoDir));
    }

    [Fact]
    public void An_unchanged_folder_takes_no_snapshot()
    {
        WriteFirstState();
        var backups = Backups();
        Assert.NotNull(backups.SnapshotIfChanged(out _));
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Null(backups.SnapshotIfChanged(out var problem));
        Assert.Null(problem);
        Assert.Single(backups.Backups());

        Write(Path.Combine("driver", "calibration_1.bin"), "second");
        Assert.NotNull(backups.SnapshotIfChanged(out _));
        Assert.Equal(2, backups.Backups().Count);
    }

    [Fact]
    public void Only_the_newest_ten_are_kept()
    {
        var backups = Backups();
        for (var i = 0; i < 12; i++)
        {
            Write("config_v0.json", $"version {i}");
            Assert.NotNull(backups.SnapshotIfChanged(out _));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var kept = backups.Backups();
        Assert.Equal(10, kept.Count);
        Assert.Equal(time.GetUtcNow() - TimeSpan.FromSeconds(1), kept[0].Taken);
        Assert.Equal(time.GetUtcNow() - TimeSpan.FromSeconds(10), kept[^1].Taken);
        Assert.Equal(10, Directory.GetDirectories(Path.Combine(BackupsDir, "monado")).Length);
    }

    [Fact]
    public void Restore_keeps_the_current_folder_first()
    {
        WriteFirstState();
        var backups = Backups();
        var first = backups.SnapshotIfChanged(out _)!;
        time.Advance(TimeSpan.FromMinutes(1));
        Write(Path.Combine("driver", "calibration_1.bin"), "replaced");
        Write("added.json", "new");

        backups.Restore(first);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["config_v0.json"] = "{ \"active\": \"tracking\" }",
                [Path.Combine("driver", "calibration_1.bin")] = "first",
            },
            Contents(MonadoDir));
        var all = backups.Backups();
        Assert.Equal(2, all.Count);

        // The newest copy is the state the restore replaced, so that it can be put back too.
        backups.Restore(all[0]);
        Assert.Equal("replaced", File.ReadAllText(Path.Combine(MonadoDir, "driver", "calibration_1.bin")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(MonadoDir, "added.json")));
    }

    [Fact]
    public void Restore_and_import_refuse_while_monado_runs()
    {
        WriteFirstState();
        var backups = Backups();
        var backup = backups.SnapshotIfChanged(out _)!;
        var zip = dir.File("export.zip");
        backups.Export(backup, zip);
        running = true;

        Assert.Equal(MonadoFolderBackups.StopFirst, Assert.Throws<MiradorException>(() => backups.Restore(backup)).Message);
        Assert.Equal(MonadoFolderBackups.StopFirst, Assert.Throws<MiradorException>(() => backups.Import(zip)).Message);
    }

    [Fact]
    public void A_locked_file_skips_the_snapshot_without_a_partial_copy()
    {
        WriteFirstState();
        var backups = Backups();

        using (new FileStream(Path.Combine(MonadoDir, "driver", "calibration_1.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(backups.SnapshotIfChanged(out var problem));
            Assert.NotNull(problem);
        }

        Assert.Empty(backups.Backups());
        var root = Path.Combine(BackupsDir, "monado");
        Assert.True(!Directory.Exists(root) || Directory.GetFileSystemEntries(root).Length == 0);
    }

    [Fact]
    public void A_folder_over_the_size_limit_is_not_copied()
    {
        Write("big.bin", new string('x', 100));
        var backups = Backups(maxBytes: 10);

        Assert.Null(backups.SnapshotIfChanged(out var problem));

        Assert.NotNull(problem);
        Assert.Empty(backups.Backups());
    }

    [Fact]
    public void Export_then_import_round_trips()
    {
        WriteFirstState();
        var backups = Backups();
        var backup = backups.SnapshotIfChanged(out _)!;
        var zip = dir.File("export.zip");
        backups.Export(backup, zip);
        time.Advance(TimeSpan.FromMinutes(1));
        Write(Path.Combine("driver", "calibration_1.bin"), "other PC");

        backups.Import(zip);

        Assert.Equal("first", File.ReadAllText(Path.Combine(MonadoDir, "driver", "calibration_1.bin")));
        Assert.Equal("{ \"active\": \"tracking\" }", File.ReadAllText(Path.Combine(MonadoDir, "config_v0.json")));

        // The folder the import replaced is kept.
        Assert.Contains(backups.Backups(), b => File.ReadAllText(Path.Combine(b.Dir, "files", "driver", "calibration_1.bin")) == "other PC");
    }

    [Fact]
    public void Import_of_another_zip_says_it_isnt_a_backup()
    {
        var backups = Backups();
        var zip = dir.File("other.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("readme.txt").Open());
            writer.Write("hello");
        }

        var notZip = dir.File("not-a-zip.zip");
        File.WriteAllText(notZip, "plain text");

        Assert.Equal(MonadoFolderBackups.NotAnExport, Assert.Throws<MiradorException>(() => backups.Import(zip)).Message);
        Assert.Equal(MonadoFolderBackups.NotAnExport, Assert.Throws<MiradorException>(() => backups.Import(notZip)).Message);
    }

    [Fact]
    public void A_clock_moved_back_never_prunes_the_copy_just_made()
    {
        var backups = Backups();
        for (var i = 0; i < 10; i++)
        {
            Write("config_v0.json", $"version {i}");
            Assert.NotNull(backups.SnapshotIfChanged(out _));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        // The clock was corrected: every copy so far looks newer than now.
        var earlier = new MonadoFolderBackups(MonadoDir, BackupsDir, NewClock(time.GetUtcNow() - TimeSpan.FromDays(1)), () => running);
        Write("config_v0.json", "after the correction");

        var made = earlier.SnapshotIfChanged(out var problem);

        Assert.Null(problem);
        Assert.NotNull(made);
        Assert.True(Directory.Exists(made.Dir));
        Assert.Contains(made, earlier.Backups());
        Assert.Equal(10, earlier.Backups().Count);

        // A restore under that clock keeps its safety copy too.
        Write("config_v0.json", "replaced by the restore");
        earlier.Restore(earlier.Backups()[0]);
        Assert.Contains(earlier.Backups(), b => File.ReadAllText(Path.Combine(b.Dir, "files", "config_v0.json")) == "replaced by the restore");
    }
}
