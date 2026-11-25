namespace Mirador.Ipc;

public static class PimaxEnvironment
{
    public const string ModuleId = "pimax";

    public static IReadOnlyList<KeyValuePair<string, string>> DreamAirVariables { get; } =
    [
        new("STEAMVR_LH_ENABLE", "1"),
    ];
}
