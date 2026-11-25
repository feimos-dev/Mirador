using System.Text.RegularExpressions;

namespace Mirador.Ipc;

public static partial class ElevatedVariables
{
    public static IReadOnlyList<string> RefusedNames { get; } =
    [
        "PATH",
        "TEMP",
        "TMP",
        "LOCALAPPDATA",
        "APPDATA",
        "USERPROFILE",
        "XDG_RUNTIME_DIR",
        "XDG_CACHE_HOME",
        "SystemRoot",
        "windir",
        "SystemDrive",
        "ComSpec",
        "PATHEXT",
        "ProgramData",
        "CommonProgramW6432",
        "PROCESSOR_ARCHITECTURE",
        "NUMBER_OF_PROCESSORS",
        "OS",
        "STEAMVR_PATH",
        "STEAMVR_LH_ENABLE",
        "VK_LAYER_PATH",
        "VK_ADD_LAYER_PATH",
        "VK_IMPLICIT_LAYER_PATH",
        "VK_ADD_IMPLICIT_LAYER_PATH",
        "VK_ICD_FILENAMES",
        "VK_DRIVER_FILES",
        "VK_ADD_DRIVER_FILES",
        "VK_INSTANCE_LAYERS",
        .. ClearedEnvironment.Names,
        .. DirectModeEnvironment.Cleared,
        .. DirectModeEnvironment.Variables.Select(v => v.Key),
        .. PimaxEnvironment.DreamAirVariables.Select(v => v.Key),
        .. SettingsNames(),
        .. HeadlessEnvironment.Variables.Select(v => v.Key),
    ];

    public static IReadOnlyList<string> RefusedPrefixes { get; } = ["ProgramFiles", "ProgramW6432", "CommonProgramFiles", "VK_LOADER_LAYERS_", "XR_"];

    public static bool IsRefused(string name) =>
        name is null
        || !PlainName().IsMatch(name)
        || RefusedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || RefusedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static string? FirstRefused(IEnumerable<KeyValuePair<string, string>> variables) =>
        variables.Select(v => v.Key).FirstOrDefault(IsRefused);

    public static bool FitInStart(string profileId, IReadOnlyList<KeyValuePair<string, string>> variables)
    {
        var start = new ElevatedStart(
            MonadoSettingsEnvironment.LighthouseWaitRange.Max, MonadoSettingsEnvironment.RenderScaleRange.Max,
            MonadoSettingsEnvironment.CompositorMinTimeRange.Max, Module: PimaxEnvironment.ModuleId,
            Build: profileId, Headless: true, ElevationFix: true, StandbyOnExit: true, ProfileId: profileId, Variables: variables);
        return LineCodec.LengthOf(new HelperRequest(HelperCommand.StartMonadoElevated, start)) <= LineCodec.MaxLineBytes;
    }

    private static List<string> SettingsNames()
    {
        var names = new List<string>();
        MonadoSettingsEnvironment.Write(
            (name, _) => names.Add(name),
            MonadoSettingsEnvironment.LighthouseWaitRange.Min,
            MonadoSettingsEnvironment.RenderScaleRange.Min,
            MonadoSettingsEnvironment.CompositorMinTimeRange.Min,
            standbyOnExit: true);
        return names;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainName();
}

public static class HeadlessEnvironment
{
    public static IReadOnlyList<KeyValuePair<string, string>> Variables { get; } =
    [
        new("XRT_COMPOSITOR_NULL", "1"),
        new("SIMULATED_ENABLE", "1"),
    ];
}
