using System.Security;
using Microsoft.Win32;

namespace Mirador.Pimax;

public interface IRegistryReader
{
    string? GetString(string hiveAndKey, string name);

    IReadOnlyList<string> SubKeyNames(string hiveAndKey);
}

public sealed class WindowsRegistryReader : IRegistryReader
{
    public string? GetString(string hiveAndKey, string name) =>
        Read(hiveAndKey, key => key.GetValue(name) as string);

    public IReadOnlyList<string> SubKeyNames(string hiveAndKey) =>
        Read<IReadOnlyList<string>>(hiveAndKey, key => key.GetSubKeyNames()) ?? [];

    private static T? Read<T>(string hiveAndKey, Func<RegistryKey, T?> read)
        where T : class
    {
        var (hive, path) = Split(hiveAndKey);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path);
            return key is null ? null : read(key);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static (RegistryHive Hive, string Path) Split(string hiveAndKey)
    {
        var slash = hiveAndKey.IndexOf('\\');
        var hiveName = slash < 0 ? hiveAndKey : hiveAndKey[..slash];
        var path = slash < 0 ? "" : hiveAndKey[(slash + 1)..];
        var hive = hiveName.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            _ => throw new ArgumentException($"Unknown registry hive in {hiveAndKey}.", nameof(hiveAndKey)),
        };
        return (hive, path);
    }
}
