using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Mirador.Ipc;

public sealed record ApprovedBuild(
    string Owner, string ProfileId, string Commit, string Folder, IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<KeyValuePair<string, string>>? Variables = null)
{
    public IReadOnlyList<KeyValuePair<string, string>> Variables { get; init; } = Variables ?? [];

    public string Manifest => Path.Combine(Folder, "openxr_monado.json");

    public string ServerDir => Path.Combine(Folder, "bin");

    public string StandInDir => Path.Combine(ServerDir, "service");
}

public interface IApprovedBuildStore
{
    ApprovedBuild? Get(string owner, string profileId);

    void Set(ApprovedBuild build);

    void Remove(string owner, string profileId);

    IReadOnlyList<string> ProfilesOf(string owner);
}

public static partial class ApprovedBuilds
{
    public const string BundledCommit = "bundled";

    public static readonly string RuntimeDll = Path.Combine("bin", "openxr_monado.dll");

    public static string Root(string appDir) => Path.Combine(appDir, "builds");

    public static string ProfileRoot(string appDir, string owner, string profileId) => Path.Combine(Root(appDir), owner, profileId);

    public static bool IsProfileId(string? id) => id is not null && ProfileIdPattern().IsMatch(id);

    public static bool IsGitCommit(string? commit) => commit is not null && GitCommitPattern().IsMatch(commit);

    public static bool IsOwner(string? sid) => sid is not null && OwnerPattern().IsMatch(sid);

    public static ApprovedBuild Approve(
        string owner, string profileId, string commit, string source, string appDir, IApprovedBuildStore store,
        IReadOnlyList<KeyValuePair<string, string>>? variables = null, Action<string>? log = null)
    {
        if (!IsOwner(owner) || !IsProfileId(profileId))
        {
            throw new InvalidOperationException("That isn't a user's profile.");
        }

        if (commit != BundledCommit && !CommitPattern().IsMatch(commit))
        {
            throw new InvalidOperationException("That isn't a commit.");
        }

        if (commit == BundledCommit && !string.IsNullOrEmpty(source))
        {
            throw new InvalidOperationException("An approval of the bundled Monado has no build folder.");
        }

        var approved = Approvable(variables);
        if (commit == BundledCommit)
        {
            var bundled = new ApprovedBuild(owner, profileId, BundledCommit, "", new Dictionary<string, string>(), approved);
            store.Set(bundled);
            RemoveWhole(ProfileRoot(appDir, owner, profileId), appDir);
            SweepLeftovers(appDir, store);
            return bundled;
        }

        var from = Path.GetFullPath(source);
        foreach (var file in new[] { "openxr_monado.json", RuntimeDll, Path.Combine("bin", "monado-service.exe") })
        {
            if (!File.Exists(Path.Combine(from, file)))
            {
                throw new InvalidOperationException($"The build in {from} has no {file}: build it again first.");
            }
        }

        var profileRoot = ProfileRoot(appDir, owner, profileId);
        var target = Path.Combine(profileRoot, commit);
        var staging = target + ".new";
        Cleanup.TryDeleteDirectory(staging);
        Copy(new DirectoryInfo(from), staging);
        WriteManifest(from, staging, log);
        PlaceStandIn(appDir, staging);
        var files = Hash(staging);

        if (Directory.Exists(target))
        {
            Directory.Move(target, target + ".old-" + Guid.NewGuid().ToString("N"));
        }

        Directory.Move(staging, target);
        var build = new ApprovedBuild(owner, profileId, commit, target, files, approved);
        store.Set(build);

        foreach (var old in Directory.GetDirectories(profileRoot).Where(d => !string.Equals(d, target, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveWhole(old, appDir);
        }

        SweepLeftovers(appDir, store);
        return build;
    }

    public static ApprovedBuild ApproveVariables(
        string owner, string profileId, string appDir, IApprovedBuildStore store, IReadOnlyList<KeyValuePair<string, string>>? variables)
    {
        if (!IsOwner(owner) || !IsProfileId(profileId))
        {
            throw new InvalidOperationException("That isn't a user's profile.");
        }

        var approved = Approvable(variables);
        var record = store.Get(owner, profileId) ?? throw new InvalidOperationException("This profile has no approved build.");
        if (record.Commit != BundledCommit && Verify(record, appDir) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        var updated = record with { Variables = approved };
        store.Set(updated);
        return updated;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Approvable(IReadOnlyList<KeyValuePair<string, string>>? variables)
    {
        IReadOnlyList<KeyValuePair<string, string>> approved = [.. variables ?? []];
        if (ElevatedVariables.FirstRefused(approved) is { } refused)
        {
            throw new InvalidOperationException($"{refused} can't be set for Monado run as administrator.");
        }

        if (approved.Any(v => v.Value is null || v.Value.Contains('\0')))
        {
            throw new InvalidOperationException("A variable's value can't hold a null character.");
        }

        return approved;
    }

    public static void Forget(string owner, string profileId, string appDir, IApprovedBuildStore store)
    {
        if (!IsOwner(owner) || !IsProfileId(profileId))
        {
            return;
        }

        store.Remove(owner, profileId);
        RemoveWhole(ProfileRoot(appDir, owner, profileId), appDir);
        SweepLeftovers(appDir, store);
    }

    public static string? Verify(ApprovedBuild build, string appDir)
    {
        if (!IsOwner(build.Owner) || !IsProfileId(build.ProfileId) || !CommitPattern().IsMatch(build.Commit)
            || !string.Equals(Path.GetFullPath(build.Folder), Path.GetFullPath(Path.Combine(ProfileRoot(appDir, build.Owner, build.ProfileId), build.Commit)), StringComparison.OrdinalIgnoreCase))
        {
            return "The approved build's record doesn't name its own folder. Approve the build again.";
        }

        if (!Directory.Exists(build.Folder) || new DirectoryInfo(build.Folder).LinkTarget is not null)
        {
            return "The approved build is gone. Approve the build again.";
        }

        var root = Path.GetFullPath(build.Folder) + Path.DirectorySeparatorChar;
        foreach (var (relative, hash) in build.Files)
        {
            var path = Path.GetFullPath(Path.Combine(build.Folder, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path) || !string.Equals(HashOf(path), hash, StringComparison.OrdinalIgnoreCase))
            {
                return $"The approved build changed since it was approved ({relative}). Approve it again.";
            }
        }

        if (!build.Files.ContainsKey("openxr_monado.json") || LibraryOf(build.Manifest) is not { } library
            || !string.Equals(library, Path.Combine(build.Folder, RuntimeDll), StringComparison.OrdinalIgnoreCase)
            || !build.Files.ContainsKey(RuntimeDll))
        {
            return "The approved build's manifest doesn't name its own DLL. Approve the build again.";
        }

        foreach (var file in Directory.EnumerateFiles(build.Folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(build.Folder, file);
            if (!build.Files.ContainsKey(relative) && IsCode(file))
            {
                return $"A file was added to the approved build since it was approved ({relative}). Approve it again.";
            }
        }

        return null;
    }

    public static bool Matches(ApprovedBuild build, string sourceInstall, Func<string, string>? hashOf = null)
    {
        hashOf ??= HashOf;
        if (build.Commit == BundledCommit || !Directory.Exists(sourceInstall) || new DirectoryInfo(sourceInstall).LinkTarget is not null)
        {
            return false;
        }

        var compared = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
        foreach (var entry in new DirectoryInfo(sourceInstall).EnumerateFileSystemInfos("*", options))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget is not null)
            {
                return false;
            }

            if (entry is not FileInfo file)
            {
                continue;
            }

            var relative = Path.GetRelativePath(sourceInstall, file.FullName);
            if (WrittenByApproval(relative))
            {
                continue;
            }

            if (!build.Files.TryGetValue(relative, out var hash) || !string.Equals(hashOf(file.FullName), hash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            compared++;
        }

        return compared == build.Files.Keys.Count(k => !WrittenByApproval(k));
    }

    private static bool WrittenByApproval(string relative) =>
        string.Equals(relative, "openxr_monado.json", StringComparison.OrdinalIgnoreCase)
        || StandInNames.FileNames.Any(name => string.Equals(relative, Path.Combine("bin", "service", name), StringComparison.OrdinalIgnoreCase));

    public static bool NamesOwnDll(ApprovedBuild build) =>
        LibraryOf(build.Manifest) is { } library && string.Equals(library, Path.Combine(build.Folder, RuntimeDll), StringComparison.OrdinalIgnoreCase);

    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static readonly string LibmonadoDll = Path.Combine("bin", "monado.dll");

    private const string LibmonadoKey = "MND_libmonado_path";

    internal static void WriteManifest(string source, string copy, Action<string>? log = null)
    {
        var manifest = Path.Combine(copy, "openxr_monado.json");
        var fields = MonadoFields(manifest, source, copy, log);
        if (!fields.Any(f => f.Key == LibmonadoKey) && File.Exists(Path.Combine(copy, LibmonadoDll)))
        {
            fields.Add(new(LibmonadoKey, LibmonadoDll));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("file_format_version", "1.0.0");
            writer.WriteStartObject("runtime");
            writer.WriteString("name", "Monado (Mirador, approved build)");
            writer.WriteString("library_path", RuntimeDll);
            foreach (var (name, value) in fields)
            {
                writer.WritePropertyName(name);
                if (value is string path)
                {
                    writer.WriteStringValue(path);
                }
                else
                {
                    ((JsonElement)value).WriteTo(writer);
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        File.WriteAllBytes(manifest, stream.ToArray());
    }

    private static List<KeyValuePair<string, object>> MonadoFields(string manifest, string source, string copy, Action<string>? log)
    {
        var fields = new List<KeyValuePair<string, object>>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(manifest));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"The build's runtime manifest can't be read ({e.Message.TrimEnd('.')}): the approved copy's manifest carries none of its fields.");
            return fields;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("runtime", out var runtime)
                || runtime.ValueKind != JsonValueKind.Object)
            {
                return fields;
            }

            foreach (var property in runtime.EnumerateObject())
            {
                if (!property.Name.StartsWith("MND_", StringComparison.Ordinal) || fields.Any(f => f.Key == property.Name))
                {
                    continue;
                }

                if (property.Name.EndsWith("_path", StringComparison.Ordinal))
                {
                    if (CopiedPath(property.Value, source, copy) is { } path)
                    {
                        fields.Add(new(property.Name, path));
                    }
                    else
                    {
                        log?.Invoke($"The approved copy's manifest leaves out {property.Name}: it doesn't name a file of the copy ({property.Value.GetRawText()}).");
                    }
                }
                else if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    fields.Add(new(property.Name, property.Value.Clone()));
                }
                else
                {
                    log?.Invoke($"The approved copy's manifest leaves out {property.Name}: it isn't text, a number or true/false.");
                }
            }
        }

        return fields;
    }

    private static string? CopiedPath(JsonElement value, string source, string copy)
    {
        try
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } path)
            {
                return null;
            }

            if (path.IndexOfAny(['/', '\\']) < 0 && !Path.IsPathRooted(path))
            {
                var inBin = Path.Combine("bin", path);
                return !path.Contains(':') && File.Exists(Path.Combine(copy, inBin)) ? inBin : null;
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            var full = Path.GetFullPath(path, root);
            if (!IsUnder(full, root))
            {
                return null;
            }

            var relative = Path.GetRelativePath(root, full);
            return !relative.Contains(':') && File.Exists(Path.Combine(copy, relative)) ? relative : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void PlaceStandIn(string appDir, string copy)
    {
        var standIn = Path.Combine(appDir, "standin", StandInNames.FileName);
        if (File.Exists(standIn))
        {
            var dir = Path.Combine(copy, "bin", "service");
            Directory.CreateDirectory(dir);
            foreach (var name in StandInNames.FileNames)
            {
                File.Copy(standIn, Path.Combine(dir, name), overwrite: true);
            }
        }
    }

    private static string? LibraryOf(string manifest)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var library = doc.RootElement.GetProperty("runtime").GetProperty("library_path").GetString();
            return library is null || Path.IsPathRooted(library) || library.Contains('/') ? null : Path.GetFullPath(library, Path.GetDirectoryName(manifest)!);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsCode(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".exe" or ".dll" or ".json" or ".manifest" or ".ps1" or ".bat" or ".cmd";

    private static void Copy(DirectoryInfo from, string to) => CopyUnder(from, to, FinalPath(from.FullName, folder: true));

    private static void CopyUnder(DirectoryInfo from, string to, string root)
    {
        if (from.LinkTarget is not null)
        {
            throw new InvalidOperationException($"{from.FullName} is a link: Mirador approves only a build's own files.");
        }

        Directory.CreateDirectory(to);
        foreach (var file in from.EnumerateFiles())
        {
            if (file.LinkTarget is not null)
            {
                throw new InvalidOperationException($"{file.FullName} is a link: Mirador approves only a build's own files.");
            }

            using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!IsUnder(FinalPath(source.SafeFileHandle), root))
            {
                throw new InvalidOperationException($"{file.FullName} leads outside the build's folder: Mirador approves only a build's own files.");
            }

            using var target = new FileStream(Path.Combine(to, file.Name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }

        foreach (var dir in from.EnumerateDirectories())
        {
            CopyUnder(dir, Path.Combine(to, dir.Name), root);
        }
    }

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string FinalPath(string path, bool folder)
    {
        const uint ReadAttributes = 0x80, ShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000;
        using var handle = CreateFileW(path, ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, folder ? BackupSemantics : 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new InvalidOperationException($"{path} can't be opened (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}).");
        }

        return FinalPath(handle);
    }

    private static string FinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        }

        if (length == 0 || length > buffer.Length)
        {
            throw new InvalidOperationException($"Mirador couldn't tell where a file of the build is (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}).");
        }

        var path = new string(buffer, 0, (int)length);
        return path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
            : path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file, [System.Runtime.InteropServices.Out] char[] path, uint length, uint flags);

    private static Dictionary<string, string> Hash(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f), HashOf, StringComparer.OrdinalIgnoreCase);

    private static void RemoveWhole(string folder, string appDir)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        var aside = Path.Combine(Root(appDir), ".old-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.Move(folder, aside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Cleanup.TryDeleteDirectory(aside);
    }

    private static void SweepLeftovers(string appDir, IApprovedBuildStore store)
    {
        var root = Root(appDir);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var left in Directory.GetDirectories(root, ".old-*"))
        {
            Cleanup.TryDeleteDirectory(left);
        }

        foreach (var user in Directory.GetDirectories(root).Where(d => IsOwner(Path.GetFileName(d))))
        {
            var owner = Path.GetFileName(user);
            foreach (var profile in Directory.GetDirectories(user))
            {
                if (IsProfileId(Path.GetFileName(profile)) && store.Get(owner, Path.GetFileName(profile)) is null)
                {
                    RemoveWhole(profile, appDir);
                }
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z")]
    private static partial Regex ProfileIdPattern();

    [GeneratedRegex(@"^S-1(-[0-9]{1,10}){1,15}\z")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex(@"^([0-9a-f]{7,40}|folder)\z")]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"^[0-9a-f]{7,40}\z")]
    private static partial Regex GitCommitPattern();
}

public sealed class RegistryApprovedBuildStore : IApprovedBuildStore
{
    public const string KeyPath = @"SOFTWARE\Mirador\ApprovedBuilds";

    private readonly RegistryHive hive;
    private readonly string keyPath;

    public RegistryApprovedBuildStore()
        : this(RegistryHive.LocalMachine, KeyPath)
    {
    }

    internal RegistryApprovedBuildStore(RegistryHive hive, string keyPath)
    {
        this.hive = hive;
        this.keyPath = keyPath;
    }

    public ApprovedBuild? Get(string owner, string profileId)
    {
        if (!ApprovedBuilds.IsOwner(owner) || !ApprovedBuilds.IsProfileId(profileId))
        {
            return null;
        }

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey($@"{keyPath}\{owner}\{profileId}");
        using var files = key?.OpenSubKey("Files");
        if (key?.GetValue("Commit") is not string commit || key.GetValue("Folder") is not string folder || files is null)
        {
            return null;
        }

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in files.GetValueNames())
        {
            if (files.GetValue(name) is string hash)
            {
                hashes[name] = hash;
            }
        }

        var variables = new List<KeyValuePair<string, string>>();
        if (key.GetValue("Variables") is string[] lines)
        {
            foreach (var line in lines)
            {
                var at = line.IndexOf('=', StringComparison.Ordinal);
                if (at > 0)
                {
                    variables.Add(new(line[..at], line[(at + 1)..]));
                }
            }
        }

        return new ApprovedBuild(owner, profileId, commit, folder, hashes, variables);
    }

    public void Set(ApprovedBuild build)
    {
        Remove(build.Owner, build.ProfileId);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.CreateSubKey($@"{keyPath}\{build.Owner}\{build.ProfileId}", writable: true);
        key.SetValue("Commit", build.Commit, RegistryValueKind.String);
        key.SetValue("Folder", build.Folder, RegistryValueKind.String);
        key.SetValue("Variables", build.Variables.Select(v => $"{v.Key}={v.Value}").ToArray(), RegistryValueKind.MultiString);
        using var files = key.CreateSubKey("Files", writable: true);
        foreach (var (name, hash) in build.Files)
        {
            files.SetValue(name, hash, RegistryValueKind.String);
        }
    }

    public void Remove(string owner, string profileId)
    {
        if (!ApprovedBuilds.IsOwner(owner) || !ApprovedBuilds.IsProfileId(profileId))
        {
            return;
        }

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        root.DeleteSubKeyTree($@"{keyPath}\{owner}\{profileId}", throwOnMissingSubKey: false);
    }

    public IReadOnlyList<string> ProfilesOf(string owner)
    {
        if (!ApprovedBuilds.IsOwner(owner))
        {
            return [];
        }

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey($@"{keyPath}\{owner}");
        return key is null ? [] : [.. key.GetSubKeyNames().Where(ApprovedBuilds.IsProfileId)];
    }
}
