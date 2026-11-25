using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mirador.Core.Monado;

public sealed record FolderBackup(DateTimeOffset Taken, string Dir);

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

    public static string DefaultMonadoDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "monado");

    private string Root => Path.Combine(backupsDir, "monado");

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

    public IReadOnlyList<FolderBackup> Backups()
    {
        lock (gate)
        {
            return List();
        }
    }

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

        var now = time.GetUtcNow();
        var name = now.ToString(StampFormat, CultureInfo.InvariantCulture);
        var dir = Path.Combine(Root, name);
        for (var n = 2; Directory.Exists(dir); n++)
        {
            dir = Path.Combine(Root, $"{name}-{n}");
        }

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

internal sealed record FolderManifest(Dictionary<string, FileEntry> Files);

internal sealed record FileEntry(long Size, string Sha256);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(FolderManifest))]
internal sealed partial class FolderBackupJsonContext : JsonSerializerContext;
