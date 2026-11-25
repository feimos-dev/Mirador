namespace Mirador.Ipc;

public static class InstalledMirador
{
    public static bool IsInstalledCopy(string? exePath, string? installDir)
    {
        if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(installDir))
        {
            return false;
        }

        try
        {
            var expected = Path.GetFullPath(Path.Combine(installDir, "Mirador.exe"));
            return string.Equals(expected, Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
