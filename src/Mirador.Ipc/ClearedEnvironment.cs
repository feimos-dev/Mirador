namespace Mirador.Ipc;

/// <summary>
/// Variables Monado never inherits from the user's environment: one lets any signed-in user's apps connect to it, the
/// other picks another direct-mode route.
/// </summary>
public static class ClearedEnvironment
{
    public static IReadOnlyList<string> Names { get; } = ["IPC_RELAXED_CONNECTION_SECURITY", "XRT_COMPOSITOR_FORCE_NVAPI_DIRECT"];
}
