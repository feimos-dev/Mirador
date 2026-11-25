using System.Security.Principal;
using Mirador.Ipc;

namespace Mirador.Core.Elevation;

public static class AllowElevatedCommand
{
    public const string Flag = "--allow-elevated-monado";

    internal const int Done = 0;
    internal const int Failed = 1;

    public static SecurityIdentifier? CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User;
    }

    public static IReadOnlyDictionary<string, string> UnsignedLighthouseFiles() =>
        UnsignedLighthouseFiles(SteamVr.SteamPathFromRegistry(), ReadTextOrNull, Directory.Exists, WinTrustSigner.SubjectOf);

    public static IReadOnlyDictionary<string, string> UnsignedLighthouseFiles(
        string? steamPath, Func<string, string?> readFile, Func<string, bool> dirExists, Func<string, string?> signer)
    {
        if (SteamVrDriverCheck.Find(steamPath, readFile, dirExists) is not { } steamVr)
        {
            return new Dictionary<string, string>();
        }

        return SteamVrDriverCheck.UnsignedFiles(steamVr, signer);
    }

    public static int Run(
        ElevatedConsent consent, SecurityIdentifier? user, Func<IReadOnlyDictionary<string, string>> unsignedFiles, Action<string> logError)
    {
        if (user is null)
        {
            logError("Mirador couldn't tell which user is running it, so Monado was not allowed to run as administrator.");
            return Failed;
        }

        IReadOnlyDictionary<string, string> files;
        try
        {
            files = unsignedFiles();
        }
        catch (IOException e)
        {
            logError(e.Message);
            return Failed;
        }
        catch (Exception e)
        {
            logError($"Mirador couldn't check SteamVR's Lighthouse files ({e.Message.Trim().TrimEnd('.')}), so Monado was not allowed to run as administrator.");
            return Failed;
        }

        try
        {
            consent.Allow(user, files);
            return Done;
        }
        catch (Exception e)
        {
            logError($"Mirador couldn't record the consent to run Monado as administrator ({e.Message.Trim().TrimEnd('.')}).");
            return Failed;
        }
    }

    private static string? ReadTextOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
