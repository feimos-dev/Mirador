namespace Mirador.Ipc;

public static class ClearedEnvironment
{
    public static IReadOnlyList<string> Names { get; } = ["IPC_RELAXED_CONNECTION_SECURITY", "XRT_COMPOSITOR_FORCE_NVAPI_DIRECT"];
}
