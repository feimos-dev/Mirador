using Mirador.Core.Monado;

namespace Mirador.Core.Runtime;

public static class RuntimeLine
{
    public static string Describe(string? activeManifest, MonadoInstall? install, bool isActive, string profileName)
    {
        if (isActive && install is not null)
        {
            return $"Monado: {profileName}";
        }

        if (string.IsNullOrWhiteSpace(activeManifest))
        {
            return "None set";
        }

        return ManifestName(activeManifest);
    }

    public static string ManifestName(string manifest) =>
        manifest.Trim().Trim('"').Split('\\', '/').LastOrDefault(part => part.Length > 0) ?? manifest;
}
