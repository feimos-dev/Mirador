using System.Globalization;
using Mirador.Ipc;

namespace Mirador.Helper.ElevatedMonado;

public sealed record UserFolders(string UserProfile, string AppData, string LocalAppData, string Temp)
{
    public static UserFolders FromBlock(IReadOnlyDictionary<string, string> block)
    {
        var profile = Get(block, "USERPROFILE") ?? "";
        var temp = Get(block, "TMP") ?? Get(block, "TEMP") ?? profile;
        return new UserFolders(
            profile,
            Get(block, "APPDATA") ?? Path.Combine(profile, "AppData", "Roaming"),
            Get(block, "LOCALAPPDATA") ?? Path.Combine(profile, "AppData", "Local"),
            temp);
    }

    private static string? Get(IReadOnlyDictionary<string, string> block, string name)
    {
        foreach (var (key, value) in block)
        {
            if (value.Length > 0 && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}

public static class ElevatedEnvironment
{
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD;.VBS;.JS;.WS;.WSF;.MSC";

    public static IReadOnlyDictionary<string, string> Build(
        ElevatedStart start, UserFolders folders, string serverDir, string steamVrPath, bool administratorProtection = false,
        IReadOnlyList<KeyValuePair<string, string>>? approvedVariables = null)
    {
        if (approvedVariables is not null && ElevatedVariables.FirstRefused(approvedVariables) is { } refused)
        {
            throw new ArgumentException($"{refused} can't be set for Monado run as administrator.", nameof(approvedVariables));
        }

        if (MonadoSettingsEnvironment.OutOfRange(start.LighthouseWaitMs, start.RenderScalePercent, start.CompositorMinTimeMs) is { } bad)
        {
            throw new ArgumentException($"The Monado setting {bad} is out of range.", nameof(start));
        }

        var moduleFixed = ModuleFixed(start.Module);
        var directModeFixed = DirectModeFixed(start.DirectMode);
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var system32 = Environment.SystemDirectory;
        var windows = Path.GetDirectoryName(system32) ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var wbem = Path.Combine(system32, "wbem");
        Set(env, "SystemRoot", windows);
        Set(env, "windir", windows);
        Set(env, "SystemDrive", (Path.GetPathRoot(windows) ?? @"C:\").TrimEnd('\\'));
        Set(env, "ComSpec", Path.Combine(system32, "cmd.exe"));
        Set(env, "PROCESSOR_ARCHITECTURE", Machine("PROCESSOR_ARCHITECTURE") ?? "AMD64");
        Set(env, "NUMBER_OF_PROCESSORS", Machine("NUMBER_OF_PROCESSORS") ?? Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        Set(env, "OS", Machine("OS") ?? "Windows_NT");
        Set(env, "PATHEXT", Machine("PATHEXT") ?? DefaultPathExt);
        Set(env, "ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Set(env, "ProgramFiles", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Set(env, "ProgramFiles(x86)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Set(env, "CommonProgramFiles", Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles));

        Set(env, "USERPROFILE", folders.UserProfile);
        Set(env, "APPDATA", folders.AppData);
        Set(env, "LOCALAPPDATA", folders.LocalAppData);
        Set(env, "TEMP", folders.Temp);
        Set(env, "TMP", folders.Temp);

        Set(env, "PATH", $"{system32};{windows};{wbem};{serverDir}");

        Set(env, "STEAMVR_PATH", steamVrPath);
        if (!start.ElevationFix || administratorProtection)
        {
            Set(env, "IPC_RELAXED_CONNECTION_SECURITY", "1");
        }

        MonadoSettingsEnvironment.Write(
            (name, value) => Set(env, name, value),
            start.LighthouseWaitMs, start.RenderScalePercent, start.CompositorMinTimeMs, start.StandbyOnExit);

        foreach (var (name, value) in directModeFixed.Concat(moduleFixed))
        {
            Set(env, name, value);
        }

        if (start.Headless)
        {
            if (start.Module.Length > 0 || start.DirectMode.Length > 0)
            {
                throw new ArgumentException("A headless Monado runs without a headset module or direct-mode layer.", nameof(start));
            }

            foreach (var (name, value) in HeadlessEnvironment.Variables)
            {
                Set(env, name, value);
            }
        }

        foreach (var (name, value) in approvedVariables ?? [])
        {
            Set(env, name, value);
        }

        return env;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ModuleFixed(string module) => module switch
    {
        "" => [],
        PimaxEnvironment.ModuleId => PimaxEnvironment.DreamAirVariables,
        _ => throw new ArgumentException($"Unknown headset module \"{module}\".", nameof(module)),
    };

    private static IReadOnlyList<KeyValuePair<string, string>> DirectModeFixed(string directMode) => directMode switch
    {
        "" => [],
        DirectModeEnvironment.Nvidia => DirectModeEnvironment.Variables,
        _ => throw new ArgumentException($"Unknown direct-mode layer \"{directMode}\".", nameof(directMode)),
    };

    private static string? Machine(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static void Set(Dictionary<string, string> env, string name, string value)
    {
        env.Remove(name);
        env[name] = value;
    }
}
