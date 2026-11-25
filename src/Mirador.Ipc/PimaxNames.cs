namespace Mirador.Ipc;

public static class PimaxNames
{
    public const string Service = "PiServiceLauncher";

    public static readonly string[] Processes =
    [
        "PimaxClient",
        "pi_server",
        "pi_overlay",
        "PiPlayService",
        "PiPlatformService_64",
        "vrss_gaze_provider",
        EyeRuntimeProcess,
    ];

    public const string EyeRuntimeProcess = "platform_runtime_XR5EYECHIP_WIN10_x64";

    public static IReadOnlyList<string> EyeBlobVariants { get; } = ["K10_RAWH", "K10_RAYH", "K11_RAWH", "K11_RAYH"];
}
