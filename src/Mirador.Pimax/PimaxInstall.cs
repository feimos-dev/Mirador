using Mirador.Ipc;

namespace Mirador.Pimax;

public sealed record PimaxInstall(string RuntimeDir, string? Version)
{
    private const string ServiceKey = @"HKLM\SYSTEM\CurrentControlSet\Services\" + PimaxNames.Service;
    private const string DisplayName = "PimaxPlay";

    private static readonly string[] UninstallKeys =
    [
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    private string PlatformRuntimeDir => Path.Combine(RuntimeDir, "EyeTrackingServer", "platform_runtime");

    public string TobiiRuntimeExe => Path.Combine(PlatformRuntimeDir, PimaxNames.EyeRuntimeProcess + ".exe");

    public string BlobPath(string variant) => Path.Combine(PlatformRuntimeDir, "config", EyeBlobVariants.FileName(variant));

    public static PimaxInstall? Locate(IRegistryReader reg)
    {
        var entry = FindUninstallEntry(reg);
        var version = entry is null ? null : NullIfBlank(reg.GetString(entry, "DisplayVersion"));

        var runtimeDir = FullFolder(ServiceImage.ProgramOf(reg.GetString(ServiceKey, "ImagePath")) is { } exe ? Path.GetDirectoryName(exe) : null)
            ?? (entry is not null && NullIfBlank(reg.GetString(entry, "InstallLocation")) is { } location
                ? FullFolder(Path.Combine(location.Trim().Trim('"'), "Runtime"))
                : null);

        return runtimeDir is null ? null : new PimaxInstall(runtimeDir, version);
    }

    private static string? FindUninstallEntry(IRegistryReader reg)
    {
        foreach (var root in UninstallKeys)
        {
            foreach (var name in reg.SubKeyNames(root))
            {
                var key = $@"{root}\{name}";
                if (string.Equals(reg.GetString(key, "DisplayName")?.Trim(), DisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }
        }

        return null;
    }

    private static string? FullFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
