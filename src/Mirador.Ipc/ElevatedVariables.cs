namespace Mirador.Ipc;

/// <summary>
/// The names a profile's own variables may not set for Monado run as administrator: names that would point it at files
/// the approval doesn't hash, or change where Monado and the games find each other. Windows' variable names ignore
/// case, so these do too. Such a profile runs as the user.
/// </summary>
public static class ElevatedVariables
{
    /// <summary>
    /// The refused names: the search path, the temp folders (the pipe's name comes from them), the user's folders, the
    /// Vulkan loader's path and layer variables, and every name of <see cref="DirectModeEnvironment.Cleared"/>. Names
    /// starting with one of <see cref="RefusedPrefixes"/> are refused too.
    /// </summary>
    public static IReadOnlyList<string> RefusedNames { get; } =
    [
        "PATH",
        "TEMP",
        "TMP",
        "LOCALAPPDATA",
        "APPDATA",
        "USERPROFILE",
        "VK_LAYER_PATH",
        "VK_ADD_LAYER_PATH",
        "VK_ICD_FILENAMES",
        "VK_DRIVER_FILES",
        "VK_ADD_DRIVER_FILES",
        "VK_INSTANCE_LAYERS",
        .. DirectModeEnvironment.Cleared,
    ];

    /// <summary>The refused beginnings: the Vulkan loader's layer filters and the OpenXR loader's variables.</summary>
    public static IReadOnlyList<string> RefusedPrefixes { get; } = ["VK_LOADER_LAYERS_", "XR_"];

    /// <summary>
    /// Whether <paramref name="name"/> may not be set for an elevated Monado, whatever its case. A name Windows can't
    /// hold as a variable (empty, or with <c>=</c> or a null character in it) is refused too.
    /// </summary>
    public static bool IsRefused(string name) =>
        string.IsNullOrEmpty(name)
        || name.Contains('=')
        || name.Contains('\0')
        || RefusedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || RefusedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first name of <paramref name="variables"/> that <see cref="IsRefused"/>; null when there is none.</summary>
    public static string? FirstRefused(IEnumerable<KeyValuePair<string, string>> variables) =>
        variables.Select(v => v.Key).FirstOrDefault(IsRefused);
}
