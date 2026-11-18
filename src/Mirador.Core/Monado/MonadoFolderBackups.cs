using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mirador.Core.Monado;

/// <summary>A dated copy of Monado's folder.</summary>
/// <param name="Taken">When it was taken, to the second, in UTC (show it with <see cref="DateTimeOffset.ToLocalTime()"/>).</param>
/// <param name="Dir">Its folder: <c>manifest.json</c> and the copied files below <c>files</c>.</param>
public sealed record FolderBackup(DateTimeOffset Taken, string Dir);

/// <summary>
/// Dated copies of Monado's folder (<c>%LOCALAPPDATA%\monado</c>: Monado's config, and whatever its drivers keep there,
/// calibrations included), in <c>&lt;backups&gt;\monado\&lt;yyyyMMdd-HHmmss&gt;\</c>, named in UTC. Each copy has a
/// <c>manifest.json</c> of every file's size and SHA-256; a copy is taken only when the folder differs from the newest
/// one, and the newest <c>keep</c> are kept. Restoring and importing copy the folder as it is first, and only while
/// Monado is stopped. One operation at a time.
/// </summary>
/// <param name="monadoDir">Monado's folder.</param>
/// <param name="backupsDir">Mirador's backups folder; the copies go in its <c>monado</c> folder.</param>
/// <param name="time">The clock, for the copies' names.</param>
/// <param name="monadoRuns">Whether Monado runs now (restoring and importing wait for it to stop); null: never.</param>
/// <param name="keep">How many copies are kept.</param>
/// <param name="maxBytes">The largest folder Mirador copies; a larger one isn't copied.</param>
public sealed class MonadoFolderBackups(
    string monadoDir, string backupsDir, TimeProvider time, Func<bool>? monadoRuns = null, int keep = 10, long maxBytes = 200_000_000)
{
    public const string StopFirst = "Stop Monado first.";

    public const string NotAnExport = "This isn't a Mirador backup of Monado's folder.";

    private const string ManifestName = "manifest.json";
    private const string FilesName = "files";
    private const string PartialSuffix = ".partial";
    private const string StampFormat = "yyyyMMdd-HHmmss";
    private const string ZipFilesPrefix = FilesName + "/";

    private readonly object gate = new();

    /// <summary>Monado's folder: <c>%LOCALAPPDATA%\monado</c>.</summary>
    public static string DefaultMonadoDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "monado");

    private string Root => Path.Combine(backupsDir, "monado");

    /// <summary>
    /// Copies Monado's folder when it differs from the newest copy. Never throws for the files: a file that can't be
    /// read (Monado or a driver holds it) or a folder over the size limit skips the copy, and nothing half-copied stays.
    /// </summary>
    /// <param name="problem">Why no copy was taken although one was due; null otherwise.</param>
    /// <returns>The new copy; null when the folder is unchanged, missing or empty, or when <paramref name="problem"/> says why.</returns>
    public FolderBackup? SnapshotIfChanged(out string? problem)
    {
        lock (gate)
        {
            problem = null;
            try
            {
                return SnapshotCore();
            }
            catch (MiradorException e)
            {
                problem = e.Message;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problem = $"Mirador couldn't back up Monado's folder ({Trimmed(e.Message)}).";
            }

            return null;
        }
    }

    /// <summary>The copies, newest first.</summary>
    public IReadOnlyList<FolderBackup> Backups()
    {
        lock (gate)
        {
            return List();
        }
    }

    /// <summary>Puts a copy back in place of Monado's folder, copying the folder as it is first.</summary>
    /// <exception cref="MiradorException">Monado runs (<see cref="StopFirst"/>), the copy is gone, or the folder as it is couldn't be copied.</exception>
    public void Restore(FolderBackup backup)
    {
        lock (gate)
        {
            RefuseWhileRunning();
            var files = Path.Combine(backup.Dir, FilesName);
            if (!File.Exists(Path.Combine(backup.Dir, ManifestName)) || !Directory.Exists(files))
            {
                throw new MiradorException($"That backup isn't there any more ({backup.Dir}).");
            }

            Replace(staging =>
            {
                foreach (var relative in Files(files))
                {
                    var target = Path.Combine(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(files, relative), target);
                }
            });
        }
    }

    /// <summary>Writes a copy into a zip, to keep or to take to another PC.</summary>
    /// <exception cref="MiradorException">The copy is gone.</exception>
    public void Export(FolderBackup backup, string zipPath)
    {
        lock (gate)
        {
            var manifest = Path.Combine(backup.Dir, ManifestName);
            var files = Path.Combine(backup.Dir, FilesName);
            if (!File.Exists(manifest) || !Directory.Exists(files))
            {
                throw new MiradorException($"That backup isn't there any more ({backup.Dir}).");
            }

            var temp = zipPath + ".tmp";
            try
            {
                using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    archive.CreateEntryFromFile(manifest, ManifestName);
                    foreach (var relative in Files(files))
                    {
                        archive.CreateEntryFromFile(Path.Combine(files, relative), ZipFilesPrefix + Portable(relative));
                    }
                }

                File.Move(temp, zipPath, overwrite: true);
            }
            catch
            {
                File.Delete(temp);
                throw;
            }
        }
    }

    /// <summary>Takes an export in place of Monado's folder, copying the folder as it is first.</summary>
    /// <exception cref="MiradorException">Monado runs (<see cref="StopFirst"/>), the zip isn't an export (<see cref="NotAnExport"/>), or the folder as it is couldn't be copied.</exception>
    public void Import(string zipPath)
    {
        lock (gate)
        {
            RefuseWhileRunning();
            Replace(staging => Extract(zipPath, staging));
        }
    }

    private FolderBackup? SnapshotCore()
    {
        if (!Directory.Exists(monadoDir))
        {
            return null;
        }

        var files = Files(monadoDir);
        if (files.Count == 0)
        {
            return null;
        }

        var total = files.Sum(f => new FileInfo(Path.Combine(monadoDir, f)).Length);
        if (total > maxBytes)
        {
            throw new MiradorException(
                $"Monado's folder is {Megabytes(total)} MB, more than the {Megabytes(maxBytes)} MB Mirador backs up, so Mirador didn't back it up.");
        }

        var current = Hash(monadoDir, files);
        if (List().FirstOrDefault() is { } newest && ReadManifest(newest.Dir) is { } kept && Same(kept, current))
        {
            return null;
        }

        // Named in UTC, so that a time zone change doesn't reorder the copies.
        var now = time.GetUtcNow();
        var name = now.ToString(StampFormat, CultureInfo.InvariantCulture);
        var dir = Path.Combine(Root, name);
        for (var n = 2; Directory.Exists(dir); n++)
        {
            dir = Path.Combine(Root, $"{name}-{n}");
        }

        // Made aside and moved into place whole: a copy cut short never shows in the list.
        var partial = dir + PartialSuffix;
        DeleteIfThere(partial);
        try
        {
            var manifest = new FolderManifest([]);
            foreach (var relative in files)
            {
                var target = Path.Combine(partial, FilesName, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = OpenShared(Path.Combine(monadoDir, relative));
                using var copy = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                manifest.Files[Portable(relative)] = CopyHashing(source, copy);
            }

            File.WriteAllText(Path.Combine(partial, ManifestName), JsonSerializer.Serialize(manifest, FolderBackupJsonContext.Default.FolderManifest));
            Directory.Move(partial, dir);
        }
        catch
        {
            DeleteQuietly(partial);
            throw;
        }

        Prune(dir);
        return new FolderBackup(TakenOf(Path.GetFileName(dir))!.Value, dir);
    }

    /// <summary>
    /// Fills a new folder beside Monado's with <paramref name="fill"/>, copies Monado's folder as it is, then puts the
    /// new folder in its place. A failure before the swap leaves Monado's folder as it was.
    /// </summary>
    private void Replace(Action<string> fill)
    {
        var staging = monadoDir + ".mirador-new";
        var old = monadoDir + ".mirador-old";
        DeleteIfThere(staging);
        try
        {
            Directory.CreateDirectory(staging);
            fill(staging);

            try
            {
                SnapshotCore();
            }
            catch (MiradorException e)
            {
                throw new MiradorException($"{e.Message} Nothing was replaced.");
            }

            DeleteIfThere(old);
            var hadFolder = Directory.Exists(monadoDir);
            if (hadFolder)
            {
                Directory.Move(monadoDir, old);
            }

            try
            {
                Directory.Move(staging, monadoDir);
            }
            catch
            {
                if (hadFolder)
                {
                    Directory.Move(old, monadoDir);
                }

                throw;
            }

            DeleteQuietly(old);
        }
        finally
        {
            DeleteQuietly(staging);
        }
    }

    /// <summary>Writes an export's files into <paramref name="staging"/>, checking each against its manifest.</summary>
    private void Extract(string zipPath, string staging)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.GetEntry(ManifestName) is not { } manifestEntry || manifestEntry.Length > 16 * 1024 * 1024)
            {
                throw new MiradorException(NotAnExport);
            }

            FolderManifest? manifest;
            using (var stream = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize(stream, FolderBackupJsonContext.Default.FolderManifest);
            }

            if (manifest?.Files is not { } expected)
            {
                throw new MiradorException(NotAnExport);
            }

            var entries = archive.Entries.Where(e => e != manifestEntry && !e.FullName.EndsWith('/')).ToList();
            var total = entries.Sum(e => e.Length);
            if (total > maxBytes || entries.Count != expected.Count)
            {
                throw new MiradorException(NotAnExport);
            }

            foreach (var entry in entries)
            {
                if (!entry.FullName.StartsWith(ZipFilesPrefix, StringComparison.Ordinal)
                    || entry.FullName[ZipFilesPrefix.Length..] is not { } relative
                    || !IsSafe(relative)
                    || !expected.TryGetValue(relative, out var want))
                {
                    throw new MiradorException(NotAnExport);
                }

                var target = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                FileEntry got;
                using (var source = entry.Open())
                using (var copy = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                {
                    got = CopyHashing(source, copy);
                }

                if (got != want)
                {
                    throw new MiradorException(NotAnExport);
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or JsonException)
        {
            throw new MiradorException(NotAnExport);
        }
    }

    /// <summary>A path inside the folder: relative, without <c>..</c>, a drive or empty parts.</summary>
    private static bool IsSafe(string relative) =>
        relative.Length > 0
        && !relative.Contains('\\')
        && !relative.Contains(':')
        && relative.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

    private List<FolderBackup> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var found = new List<FolderBackup>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(dir);
            if (!name.EndsWith(PartialSuffix, StringComparison.Ordinal) && TakenOf(name) is { } taken
                && File.Exists(Path.Combine(dir, ManifestName)))
            {
                found.Add(new FolderBackup(taken, dir));
            }
        }

        return [.. found.OrderByDescending(b => b.Taken).ThenByDescending(b => b.Dir, StringComparer.Ordinal)];
    }

    /// <summary>When a copy named <paramref name="name"/> was taken, in UTC; null for another name.</summary>
    private static DateTimeOffset? TakenOf(string name)
    {
        if (name.Length < StampFormat.Length
            || !DateTime.TryParseExact(name[..StampFormat.Length], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            || (name.Length > StampFormat.Length && name[StampFormat.Length] != '-'))
        {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.Zero);
    }

    /// <summary>
    /// Deletes the oldest copies beyond <c>keep</c>, never <paramref name="made"/>, the one just taken: after the clock
    /// was set back it sorts before older ones. One that can't be deleted now goes another time.
    /// </summary>
    private void Prune(string made)
    {
        var others = List().Where(b => !string.Equals(b.Dir, made, StringComparison.OrdinalIgnoreCase));
        foreach (var old in others.Skip(Math.Max(keep - 1, 0)))
        {
            DeleteQuietly(old.Dir);
        }
    }

    private void RefuseWhileRunning()
    {
        if (monadoRuns?.Invoke() == true)
        {
            throw new MiradorException(StopFirst);
        }
    }

    /// <summary>The files below <paramref name="folder"/>, as paths relative to it, in a fixed order; links aren't followed.</summary>
    private static List<string> Files(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Select(f => Path.GetRelativePath(folder, f))
            .Order(StringComparer.Ordinal)];

    private static FolderManifest Hash(string folder, IReadOnlyList<string> files)
    {
        var manifest = new FolderManifest([]);
        foreach (var relative in files)
        {
            using var source = OpenShared(Path.Combine(folder, relative));
            manifest.Files[Portable(relative)] = CopyHashing(source, Stream.Null);
        }

        return manifest;
    }

    private static FolderManifest? ReadManifest(string dir)
    {
        try
        {
            // A manifest without its file list (such as "{}") counts as none.
            return JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(dir, ManifestName)), FolderBackupJsonContext.Default.FolderManifest) is { Files: not null } manifest
                ? manifest
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Same(FolderManifest a, FolderManifest b) =>
        a.Files.Count == b.Files.Count && a.Files.All(kv => b.Files.TryGetValue(kv.Key, out var other) && other == kv.Value);

    /// <summary>Opens a file for reading while others may read and write it too; one held exclusively throws <see cref="IOException"/>.</summary>
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static FileEntry CopyHashing(Stream source, Stream target)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            target.Write(buffer, 0, read);
            size += read;
        }

        return new FileEntry(size, Convert.ToHexString(sha.GetHashAndReset()));
    }

    private static string Portable(string relative) => relative.Replace(Path.DirectorySeparatorChar, '/');

    private static long Megabytes(long bytes) => (bytes + 999_999) / 1_000_000;

    private static string Trimmed(string message) => message.Trim().TrimEnd('.');

    private static void DeleteIfThere(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void DeleteQuietly(string dir)
    {
        try
        {
            DeleteIfThere(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A copy's <c>manifest.json</c>: each file's path (with <c>/</c>) and what it holds.</summary>
internal sealed record FolderManifest(Dictionary<string, FileEntry> Files);

internal sealed record FileEntry(long Size, string Sha256);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(FolderManifest))]
internal sealed partial class FolderBackupJsonContext : JsonSerializerContext;
