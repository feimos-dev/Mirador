namespace Mirador.Ipc;

public static class DirectModeEnvironment
{
    public const string Nvidia = "nvidia";

    public const string ManifestName = "mirador_direct_mode.json";

    public static IReadOnlyList<KeyValuePair<string, string>> Variables { get; } =
    [
        new("MIRADOR_DIRECT_MODE", "1"),
        new("DISABLE_RTSS_LAYER", "1"),
        new("VK_LOADER_LAYERS_ALLOW", "VK_LAYER_MIRADOR_direct_mode"),
        new("XRT_COMPOSITOR_FORCE_VK_DISPLAY", "0"),
        new("XRT_COMPOSITOR_USE_PRESENT_WAIT", "1"),
    ];

    public static IReadOnlyList<string> Cleared { get; } =
    [
        "DISABLE_MIRADOR_DIRECT_MODE",
        "MIRADOR_DIRECT_MODE_TEST_NO_EDID",
        "MIRADOR_DIRECT_MODE_IGNORE_HOLDERS",
    ];
}
