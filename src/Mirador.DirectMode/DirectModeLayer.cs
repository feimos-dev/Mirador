using Mirador.Core;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.DirectMode;

public sealed record LayerFiles(string ManifestDir, string CheckExe);

public static class DirectModeLayer
{
    public const string ManifestName = DirectModeEnvironment.ManifestName;
    public const string DllName = "mirador_direct_mode.dll";
    public const string CheckExeName = "mirador-display-check.exe";

    public static IReadOnlyList<EnvVar> Environment(string bundledDir)
    {
        Locate(bundledDir);
        return
        [
            .. DirectModeEnvironment.Variables.Select(v => new EnvVar(v.Key, v.Value)),
            .. DirectModeEnvironment.Cleared.Select(name => new EnvVar(name, "")),
        ];
    }

    private static readonly string[] LayerFileNames = [ManifestName, DllName, CheckExeName];

    public static LayerFiles Locate(string bundledDir)
    {
        if (string.IsNullOrWhiteSpace(bundledDir))
        {
            throw new MiradorException("This Mirador doesn't know where the direct-mode layer that comes with it is. Reinstall Mirador.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundledDir));
        if (!Directory.Exists(root))
        {
            throw new MiradorException($"The direct-mode layer that comes with Mirador is missing ({root}). Reinstall Mirador.");
        }

        var missing = LayerFileNames.Where(name => !File.Exists(Path.Combine(root, name))).ToList();
        if (missing.Count > 0)
        {
            throw new MiradorException(
                $"The direct-mode layer that comes with Mirador is incomplete: {string.Join(" and ", missing)} {(missing.Count == 1 ? "is" : "are")} missing from {root}. Reinstall Mirador.");
        }

        return new LayerFiles(root, Path.Combine(root, CheckExeName));
    }
}
