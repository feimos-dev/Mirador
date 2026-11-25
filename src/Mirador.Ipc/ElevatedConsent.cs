using System.Security.Principal;
using Microsoft.Win32;

namespace Mirador.Ipc;

public interface IConsentValues
{
    bool Has(string sid);

    void Set(string sid);

    void Delete(string sid);

    string[]? Files(string sid);

    void SetFiles(string sid, string[] lines);

    void DeleteFiles(string sid);
}

public sealed class ElevatedConsent(IConsentValues values)
{
    public bool IsAllowed(SecurityIdentifier sid) => values.Has(sid.Value);

    public void Allow(SecurityIdentifier sid, IReadOnlyDictionary<string, string> unsignedFiles)
    {
        var lines = new List<string>(unsignedFiles.Count);
        foreach (var (name, sha256) in unsignedFiles.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsFileName(name))
            {
                throw new ArgumentException($"\"{name}\" isn't a file name.", nameof(unsignedFiles));
            }

            if (!IsSha256(sha256))
            {
                throw new ArgumentException($"The hash of {name} isn't a lower-case hex SHA-256.", nameof(unsignedFiles));
            }

            lines.Add(name + "=" + sha256);
        }

        if (lines.Count == 0)
        {
            values.DeleteFiles(sid.Value);
        }
        else
        {
            values.SetFiles(sid.Value, [.. lines]);
        }

        values.Set(sid.Value);
    }

    public IReadOnlyDictionary<string, string> ApprovedFiles(SecurityIdentifier sid)
    {
        var approved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in values.Files(sid.Value) ?? [])
        {
            var eq = line.LastIndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = line[..eq];
            var sha256 = line[(eq + 1)..];
            if (IsFileName(name) && IsSha256(sha256))
            {
                approved.TryAdd(name, sha256);
            }
        }

        return approved;
    }

    public void Revoke(SecurityIdentifier sid)
    {
        values.Delete(sid.Value);
        values.DeleteFiles(sid.Value);
    }

    private static bool IsFileName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name is not "." and not "..";

    private static bool IsSha256(string text) =>
        text.Length == 64 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class RegistryConsentValues : IConsentValues
{
    public const string KeyPath = @"SOFTWARE\Mirador\ElevatedMonado";

    public const string FilesKeyPath = @"SOFTWARE\Mirador\ElevatedMonadoFiles";

    private readonly RegistryHive hive;
    private readonly string keyPath;
    private readonly string filesKeyPath;

    public RegistryConsentValues()
        : this(RegistryHive.LocalMachine, KeyPath, FilesKeyPath)
    {
    }

    internal RegistryConsentValues(RegistryHive hive, string keyPath, string filesKeyPath)
    {
        this.hive = hive;
        this.keyPath = keyPath;
        this.filesKeyPath = filesKeyPath;
    }

    public bool Has(string sid)
    {
        using var root = Root();
        using var key = root.OpenSubKey(keyPath);
        return key?.GetValue(sid) is int value && value == 1;
    }

    public void Set(string sid)
    {
        using var root = Root();
        using var key = root.CreateSubKey(keyPath, writable: true);
        key.SetValue(sid, 1, RegistryValueKind.DWord);
    }

    public void Delete(string sid) => DeleteValue(keyPath, sid);

    public string[]? Files(string sid)
    {
        using var root = Root();
        using var key = root.OpenSubKey(filesKeyPath);
        if (key is null || Array.IndexOf(key.GetValueNames(), sid) < 0 || key.GetValueKind(sid) != RegistryValueKind.MultiString)
        {
            return null;
        }

        return key.GetValue(sid) as string[];
    }

    public void SetFiles(string sid, string[] lines)
    {
        using var root = Root();
        using var key = root.CreateSubKey(filesKeyPath, writable: true);
        key.SetValue(sid, lines, RegistryValueKind.MultiString);
    }

    public void DeleteFiles(string sid) => DeleteValue(filesKeyPath, sid);

    private RegistryKey Root() => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    private void DeleteValue(string path, string sid)
    {
        using var root = Root();
        bool empty;
        using (var key = root.OpenSubKey(path, writable: true))
        {
            if (key is null)
            {
                return;
            }

            key.DeleteValue(sid, throwOnMissingValue: false);
            empty = key.ValueCount == 0 && key.SubKeyCount == 0;
        }

        if (empty)
        {
            root.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
    }
}
