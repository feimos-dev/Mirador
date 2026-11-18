using System.Text.RegularExpressions;

namespace Mirador.Ipc;

/// <summary>
/// The names a profile's own variables may not set for Monado run as administrator: names that would point it at files
/// the approval doesn't hash, change where Monado and the games find each other, or replace a value the helper sets
/// itself (some only after a check, such as SteamVR's folder). Windows' variable names ignore case, so these do too, and
/// only plain ASCII names are allowed: the C runtime's narrow environment, through which Monado reads its options, maps
/// look-alike characters (fullwidth letters, the Kelvin sign) onto ASCII ones. Such a profile runs as the user.
/// </summary>
public static partial class ElevatedVariables
{
    /// <summary>
    /// The refused names: the search path, the temp and user folders (the pipe's name comes from the temp folder, or from
    /// the XDG folders before it), Windows' own folders and settings the helper sets, SteamVR's folder and Lighthouse
    /// switch, the Vulkan loader's path and layer variables, every name of <see cref="DirectModeEnvironment.Cleared"/>, the
    /// module's and the layer's fixed tables, Monado's settings and the headless run's variables. Names starting with one
    /// of <see cref="RefusedPrefixes"/> are refused too.
    /// </summary>
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
        .. DirectModeEnvironment.Cleared,
        .. DirectModeEnvironment.Variables.Select(v => v.Key),
        .. DreamAirEnvironment.ModuleVariables.Select(v => v.Key),
        .. SettingsNames(),
        .. HeadlessEnvironment.Variables.Select(v => v.Key),
    ];

    /// <summary>
    /// The refused beginnings: Windows' program folders (<c>ProgramFiles</c>, <c>ProgramFiles(x86)</c>,
    /// <c>ProgramW6432</c>, <c>CommonProgramFiles</c>…; <c>CommonProgramW6432</c> is in <see cref="RefusedNames"/>),
    /// the Vulkan loader's layer filters and the OpenXR loader's variables.
    /// </summary>
    public static IReadOnlyList<string> RefusedPrefixes { get; } = ["ProgramFiles", "ProgramW6432", "CommonProgramFiles", "VK_LOADER_LAYERS_", "XR_"];

    /// <summary>
    /// Whether <paramref name="name"/> may not be set for an elevated Monado, whatever its case. Anything but plain ASCII
    /// letters, digits and <c>_</c> is refused.
    /// </summary>
    public static bool IsRefused(string name) =>
        name is null
        || !PlainName().IsMatch(name)
        || RefusedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || RefusedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first name of <paramref name="variables"/> that <see cref="IsRefused"/>; null when there is none.</summary>
    public static string? FirstRefused(IEnumerable<KeyValuePair<string, string>> variables) =>
        variables.Select(v => v.Key).FirstOrDefault(IsRefused);

    /// <summary>Every name <see cref="MonadoSettingsEnvironment.Write"/> may write, taken from it so the two can't drift.</summary>
    private static List<string> SettingsNames()
    {
        var names = new List<string>();
        MonadoSettingsEnvironment.Write(
            (name, _) => names.Add(name),
            MonadoSettingsEnvironment.LighthouseWaitRange.Min,
            MonadoSettingsEnvironment.RenderScaleRange.Min,
            MonadoSettingsEnvironment.CompositorMinTimeRange.Min,
            exitOnDisconnect: true,
            standbyOnExit: true);
        return names;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainName();
}

/// <summary>Verify's headless run: Monado's null compositor and its simulated headset, no headset and no display.</summary>
public static class HeadlessEnvironment
{
    public static IReadOnlyList<KeyValuePair<string, string>> Variables { get; } =
    [
        new("XRT_COMPOSITOR_NULL", "1"),
        new("SIMULATED_ENABLE", "1"),
    ];
}
