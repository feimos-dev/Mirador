namespace Mirador.Ipc;

/// <summary>
/// The Pimax module's id in a helper request: the helper writes the Dream Air's variables for it, the only Pimax model
/// with variables of its own. Kept here so the unelevated path (the module) and the elevated one (the helper) write the
/// same table.
/// </summary>
public static class PimaxEnvironment
{
    public const string ModuleId = "pimax";

    /// <summary>The Dream Air's variables: the switch for SteamVR's Lighthouse driver, an upstream Monado variable.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> DreamAirVariables { get; } =
    [
        new("STEAMVR_LH_ENABLE", "1"),
    ];
}
