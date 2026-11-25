using System.Security;
using Microsoft.Win32;

namespace Mirador.Ipc;

public static class SteamVr
{
    private const string SteamVrAppId = "250820";

    public static string? FindInstall(
        string? steamPath, string? steamVrPathEnv, Func<string, string?> readFile, Func<string, bool> dirExists)
    {
        if (!string.IsNullOrWhiteSpace(steamVrPathEnv))
        {
            return Resolve(steamVrPathEnv);
        }

        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return null;
        }

        var vdfPath = Resolve(steamPath, "steamapps", "libraryfolders.vdf");
        if (vdfPath is null || readFile(vdfPath) is not { } text)
        {
            return null;
        }

        var libraries = Vdf.Parse(text)?.Get("libraryfolders");
        if (libraries is null)
        {
            return null;
        }

        foreach (var (_, library) in libraries.Members)
        {
            if (library.Get("apps")?.Get(SteamVrAppId) is null
                || library.Get("path")?.Text is not { } libraryPath
                || string.IsNullOrWhiteSpace(libraryPath))
            {
                continue;
            }

            var candidate = Resolve(libraryPath, "steamapps", "common", "SteamVR");
            if (candidate is not null && dirExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static string DriverLighthouse(string steamVrDir) =>
        Path.Combine(steamVrDir, "drivers", "lighthouse", "bin", "win64", "driver_lighthouse.dll");

    public static string DriverLighthouseBinDir(string steamVrDir) =>
        Path.Combine(steamVrDir, "drivers", "lighthouse", "bin", "win64");

    public static string? SteamPathFromRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamPath") as string;
    }

    private static string? Resolve(params string[] parts)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(parts));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }
    }
}
