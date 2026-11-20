namespace Mirador.Ipc;

/// <summary>
/// The Monado variables that turn the NVIDIA direct-mode layer on, whichever headset module a profile has: the layer
/// itself, RivaTuner's overlay layer kept out of the way, Monado's compositor on the layer's display with its present
/// wait. Kept here so the unelevated path (the session) and the elevated one (the helper) write the same table. No
/// layer path: the layer that comes with Mirador is registered, the only one Mirador uses.
/// </summary>
public static class DirectModeEnvironment
{
    /// <summary>The NVIDIA layer's name in a helper request.</summary>
    public const string Nvidia = "nvidia";

    public static IReadOnlyList<KeyValuePair<string, string>> Variables { get; } =
    [
        new("MIRADOR_DIRECT_MODE", "1"),
        new("DISABLE_RTSS_LAYER", "1"),
        new("VK_LOADER_LAYERS_ALLOW", "VK_LAYER_MIRADOR_direct_mode"),
        new("XRT_COMPOSITOR_FORCE_VK_DISPLAY", "0"),
        new("XRT_COMPOSITOR_USE_PRESENT_WAIT", "1"),
    ];

    /// <summary>
    /// Variables a shell may have left set that would change the layer, taken out of the environment Monado inherits
    /// from the user (the elevated environment inherits nothing): the layer switched off, and its test switches.
    /// </summary>
    public static IReadOnlyList<string> Cleared { get; } =
    [
        "DISABLE_MIRADOR_DIRECT_MODE",
        "MIRADOR_DIRECT_MODE_TEST_NO_EDID",
        "MIRADOR_DIRECT_MODE_IGNORE_HOLDERS",
    ];
}
