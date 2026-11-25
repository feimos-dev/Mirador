using Mirador.Ipc;

namespace Mirador.Helper;

public interface IMonadoInstaller
{
    string? CurrentManifest();

    bool IsMiradors(string manifest);

    string? CurrentServerDir();

    Task<HelperReply> InstallAsync(string version, CancellationToken ct);

    HelperReply RollBack();
}

public sealed class MonadoInstaller(HttpClient http, string monadoRoot, string standInExe, IRuntimeRegistry registry, string userAgentVersion)
    : IMonadoInstaller
{
    public const long MaxZipBytes = 512L * 1024 * 1024;

    public const long MaxUnpackedBytes = 2L * 1024 * 1024 * 1024;

    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private readonly object gate = new();

    private readonly SemaphoreSlim installing = new(1, 1);

    public static MonadoInstaller ForInstall(HttpClient http, IRuntimeRegistry registry, string userAgentVersion) => new(
        http,
        Path.Combine(HelperHandler.DefaultInstallDir, "monado"),
        Path.Combine(HelperHandler.DefaultInstallDir, "standin", StandInNames.FileName),
        registry,
        userAgentVersion);

    public string? CurrentManifest()
    {
        var current = MiradorMonado.Read(monadoRoot).Current;
        var manifest = current is null ? null : MiradorMonado.ManifestOf(monadoRoot, current);
        return manifest is not null && File.Exists(manifest) ? manifest : null;
    }

    public bool IsMiradors(string manifest) => MiradorMonado.IsManifestOf(monadoRoot, manifest);

    public string? CurrentServerDir() =>
        CurrentManifest() is { } manifest ? Path.Combine(Path.GetDirectoryName(manifest)!, "bin") : null;

    public async Task<HelperReply> InstallAsync(string version, CancellationToken ct)
    {
        if (!MiradorMonado.IsVersion(version))
        {
            return new HelperReply(false, "That isn't a version of the bundled Monado.");
        }

        await installing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await InstallOneAsync(version, ct).ConfigureAwait(false);
        }
        finally
        {
            installing.Release();
        }
    }

    private async Task<HelperReply> InstallOneAsync(string version, CancellationToken ct)
    {
        lock (gate)
        {
            var kept = MiradorMonado.Read(monadoRoot);
            if (kept.Current == version && File.Exists(MiradorMonado.ManifestOf(monadoRoot, version)))
            {
                return new HelperReply(true, Value: version);
            }

            if (kept.Previous == version && File.Exists(MiradorMonado.ManifestOf(monadoRoot, version)))
            {
                return RollBack();
            }
        }

        var releases = await MonadoReleaseList.FetchAsync(http, userAgentVersion, ct).ConfigureAwait(false);
        if (releases is null)
        {
            return new HelperReply(false, "GitHub didn't answer, so the bundled Monado wasn't installed. Try again later.");
        }

        if (releases.FirstOrDefault(r => r.Version == version) is not { } release)
        {
            return new HelperReply(false, $"{version} isn't one of the bundled Monado's builds on GitHub (any more), so it wasn't installed.");
        }

        if (!File.Exists(standInExe))
        {
            return new HelperReply(false, $"This Mirador has no stand-in (standin\\{StandInNames.FileName}). Reinstall Mirador.");
        }

        var work = Path.Combine(monadoRoot, ".download");
        var zip = Path.Combine(work, version + ".zip");
        var part = Path.Combine(work, version);
        try
        {
            Directory.CreateDirectory(work);
            await CheckedDownload.ToFileAsync(
                http, new Uri(release.ZipUrl), zip, MaxZipBytes, release.ZipSha256, "the bundled Monado", m => new MiradorInstallException(m), ct, IdleTimeout)
                .ConfigureAwait(false);
            Cleanup.TryDeleteDirectory(part);
            CheckedDownload.Unpack(zip, part, MaxUnpackedBytes, "the bundled Monado", m => new MiradorInstallException(m));
            Check(part);
            var standInDir = Directory.CreateDirectory(Path.Combine(part, "bin", "service")).FullName;
            foreach (var name in StandInNames.FileNames)
            {
                File.Copy(standInExe, Path.Combine(standInDir, name), overwrite: true);
            }

            lock (gate)
            {
                return MakeCurrent(version, part);
            }
        }
        catch (Exception e) when (e is MiradorInstallException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new HelperReply(false, e is MiradorInstallException ? e.Message : $"The bundled Monado couldn't be installed ({e.Message.TrimEnd('.')}).");
        }
        finally
        {
            Cleanup.TryDelete(zip);
            Cleanup.TryDeleteDirectory(part);
        }
    }

    public HelperReply RollBack()
    {
        lock (gate)
        {
            var versions = MiradorMonado.Read(monadoRoot);
            if (versions.Previous is not { } previous || !File.Exists(MiradorMonado.ManifestOf(monadoRoot, previous)))
            {
                return new HelperReply(false, "There is no earlier version of the bundled Monado to go back to.");
            }

            MiradorMonado.Write(monadoRoot, new MiradorMonadoVersions(previous, versions.Current));
            FollowActive(versions.Current, previous);
            return new HelperReply(true, Value: previous);
        }
    }

    private HelperReply MakeCurrent(string version, string part)
    {
        var versions = MiradorMonado.Read(monadoRoot);
        var folder = MiradorMonado.FolderOf(monadoRoot, version);
        if (versions.Current == version && File.Exists(MiradorMonado.ManifestOf(monadoRoot, version)))
        {
            return new HelperReply(true, Value: version);
        }

        if (Directory.Exists(folder))
        {
            var aside = Path.Combine(monadoRoot, ".download", version + ".old");
            Cleanup.TryDeleteDirectory(aside);
            Directory.Move(folder, aside);
            Cleanup.TryDeleteDirectory(aside);
        }

        Directory.Move(part, folder);
        var previous = versions.Current is { } current && current != version ? current : versions.Previous;
        MiradorMonado.Write(monadoRoot, new MiradorMonadoVersions(version, previous));
        registry.SetAvailable(MiradorMonado.ManifestOf(monadoRoot, version), available: true);
        FollowActive(versions.Current, version);
        Prune(version, previous);
        return new HelperReply(true, Value: version);
    }

    private void FollowActive(string? from, string to)
    {
        if (registry.GetActive() is not { } active)
        {
            return;
        }

        var full = Path.GetFullPath(active);
        var wasMiradors = from is not null && string.Equals(full, MiradorMonado.ManifestOf(monadoRoot, from), StringComparison.OrdinalIgnoreCase);
        if (wasMiradors)
        {
            registry.SetActive(MiradorMonado.ManifestOf(monadoRoot, to));
        }
    }

    private void Prune(string current, string? previous)
    {
        var work = Path.Combine(monadoRoot, ".download");
        if (Directory.Exists(work))
        {
            foreach (var left in Directory.GetDirectories(work, "*.pruned-*").Concat(Directory.GetDirectories(work, "*.old")))
            {
                Cleanup.TryDeleteDirectory(left);
            }
        }

        foreach (var folder in Directory.GetDirectories(monadoRoot))
        {
            var name = Path.GetFileName(folder);
            if (!MiradorMonado.IsVersion(name) || name == current || name == previous)
            {
                continue;
            }

            var aside = Path.Combine(work, name + ".pruned-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(work);
                Directory.Move(folder, aside);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            registry.SetAvailable(MiradorMonado.ManifestOf(monadoRoot, name), available: false);
            Cleanup.TryDeleteDirectory(aside);
        }
    }

    private static void Check(string folder)
    {
        foreach (var file in new[] { "openxr_monado.json", Path.Combine("bin", "openxr_monado.dll"), Path.Combine("bin", "monado-service.exe") })
        {
            if (!File.Exists(Path.Combine(folder, file)))
            {
                throw new MiradorInstallException($"The bundled Monado's download has no {file}, so it was not used.");
            }
        }

        if (ManifestProblem(folder) is { } problem)
        {
            throw new MiradorInstallException($"The bundled Monado's download has a manifest that {problem}, so it was not used.");
        }
    }

    internal static string? ManifestProblem(string folder)
    {
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "openxr_monado.json")));
            var runtime = doc.RootElement.GetProperty("runtime");
            var library = runtime.GetProperty("library_path").GetString();
            if (library is null || Path.IsPathRooted(library)
                || !string.Equals(Path.GetFullPath(library, root), Path.Combine(root, "bin", "openxr_monado.dll"), StringComparison.OrdinalIgnoreCase))
            {
                return "doesn't name its own bin\\openxr_monado.dll";
            }

            foreach (var property in runtime.EnumerateObject())
            {
                if (property.Name.EndsWith("_path", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    && (property.Value.GetString() is not { } path || Path.IsPathRooted(path)
                        || !Path.GetFullPath(path, root).StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                {
                    return $"names a file outside its folder ({property.Name})";
                }
            }

            return null;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or IOException or ArgumentException)
        {
            return "can't be read";
        }
    }

    private sealed class MiradorInstallException(string message) : Exception(message);
}
