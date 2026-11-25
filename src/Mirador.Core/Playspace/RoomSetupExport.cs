using Mirador.Core.Lighthouse;
using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Playspace;

public static class RoomSetupExport
{
    public const string Question = "Mirador will write this room setup into SteamVR's settings. A copy is kept.";

    public const string WallsCopied =
        "SteamVR's play-area walls for this space are copied from another space; run SteamVR's Room Setup to draw them for this room.";

    private const string NoSetup = "Mirador has no room setup of this space for SteamVR's Lighthouse tracking. Set it up first.";

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

        try
        {
            return setup.Base is { } taken && !taken.SameAs(SteamVrRoom.BaseNow(steamDir)) ? RoomSetupWarning.SteamVrChanged : null;
        }
        catch (MiradorException e)
        {
            return e.Message;
        }
    }

    public static string QuestionFor(string steamDir, string universe) =>
        SteamVrRoom.HasRoomSetup(steamDir, universe) ? Question : $"{Question} {WallsCopied}";

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
