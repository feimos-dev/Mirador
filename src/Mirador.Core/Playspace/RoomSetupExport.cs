using Mirador.Core.Lighthouse;
using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Playspace;

/// <summary>
/// "Export to SteamVR": Mirador's room setup of the Lighthouse space in use, for Monado's <c>steamvr_lh</c> origin,
/// written into SteamVR's room setup (<see cref="SteamVrRoom.Export"/>), after which Mirador applies nothing on top in
/// that space. Never both.
/// </summary>
public static class RoomSetupExport
{
    /// <summary>The question asked before Mirador writes into SteamVR's room setup.</summary>
    public const string Question = "Mirador will write this room setup into SteamVR's settings. A copy is kept.";

    /// <summary>Added to the question when SteamVR has no room setup of the space yet: its entry is a copy of another's.</summary>
    public const string WallsCopied =
        "SteamVR's play-area walls for this space are copied from another space; run SteamVR's Room Setup to draw them for this room.";

    private const string NoSetup = "Mirador has no room setup of this space for SteamVR's Lighthouse tracking. Set it up first.";

    /// <summary>
    /// Why the export can't go ahead now; null when it can. SteamVR must be closed (it rewrites its file when it exits),
    /// then Monado stopped (<c>steamvr_lh</c> reads the file once per start, while Mirador's offset is live: a
    /// measurement before the next start would count the setup twice). Reads files.
    /// </summary>
    public static string? Refusal(bool steamVrRunning, bool monadoRunning, string? steamDir, string? universe, RoomSetupStore store)
    {
        if (steamVrRunning)
        {
            return "Close SteamVR first.";
        }

        if (monadoRunning)
        {
            return "Stop Monado first.";
        }

        if (steamDir is null)
        {
            return "Steam isn't installed.";
        }

        if (universe is null)
        {
            return "Mirador can't tell which Lighthouse space is in use, so it can't export.";
        }

        if (SteamVrRoom.ExportRefusal(steamDir, universe) is { } refusal)
        {
            return refusal;
        }

        if (RoomSetupApplier.InUse(store.All(), SteamVrRoom.LighthouseOrigin, universe) is not { } setup)
        {
            return NoSetup;
        }

        // The export writes the setup on top of SteamVR's room setup now: only right when it was measured on that one.
        try
        {
            return setup.Base is { } taken && !taken.SameAs(SteamVrRoom.BaseNow(steamDir)) ? RoomSetupWarning.SteamVrChanged : null;
        }
        catch (MiradorException e)
        {
            return e.Message;
        }
    }

    /// <summary>The question to ask; it says so when the space's entry in SteamVR's file will be a copy of another's.</summary>
    public static string QuestionFor(string steamDir, string universe) =>
        SteamVrRoom.HasRoomSetup(steamDir, universe) ? Question : $"{Question} {WallsCopied}";

    /// <summary>
    /// Exports the setup that applies in <paramref name="universe"/> (<see cref="RoomSetupApplier.InUse"/>: the space's
    /// own, else the one taken without a space), then keeps an offset of none for the space, noting SteamVR's new room
    /// setup as its base, so that a setup taken without a space no longer applies on top of SteamVR's in it; it stays for
    /// the other spaces.
    /// </summary>
    /// <returns>The copy of SteamVR's file as it was.</returns>
    /// <exception cref="MiradorException">Nothing was changed, or (said so) both may apply.</exception>
    public static string Run(string steamDir, string backupDir, string universe, RoomSetupStore store, TimeProvider time)
    {
        var setup = RoomSetupApplier.InUse(store.All(), SteamVrRoom.LighthouseOrigin, universe) ?? throw new MiradorException(NoSetup);
        return SteamVrRoom.Export(
            steamDir,
            backupDir,
            universe,
            setup.Offset,
            () => store.Set(new RoomSetup(
                SteamVrRoom.LighthouseOrigin, universe, MonadoPose.Identity, time.GetLocalNow(), SteamVrRoom.BaseNow(steamDir))),
            time);
    }
}
