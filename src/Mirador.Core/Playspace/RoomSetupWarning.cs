using Mirador.Core.BaseStations;
using Mirador.Core.Lighthouse;

namespace Mirador.Core.Playspace;

public static class RoomSetupWarning
{
    public const string NoSetup =
        "No room setup for this space: the floor and forward direction may be wrong in games. Set it up on the Lighthouse page.";

    public const string OtherSpace =
        "SteamVR's room setup of another space is used: the floor and forward direction may be wrong in games. Set it up on the Lighthouse page.";

    public const string SteamVrChanged =
        "SteamVR's room setup changed since Mirador's: the floor and forward direction may be wrong in games. Measure again on the Lighthouse page.";

    public static bool BaseChanged(IReadOnlyList<RoomSetup> setups, string? universe, SteamVrBase? now, bool profileUsesSteamVrLh) =>
        profileUsesSteamVrLh
        && now is not null
        && RoomSetupApplier.InUse(setups, SteamVrRoom.LighthouseOrigin, universe)?.Base is { } taken
        && !taken.SameAs(now);

    public static string? For(UniversePick pick, IReadOnlySet<string> steamVrRoomSetups, bool miradorHasSetup, bool profileUsesSteamVrLh)
    {
        if (miradorHasSetup)
        {
            return null;
        }

        if (!profileUsesSteamVrLh)
        {
            return NoSetup;
        }

        if (pick.DiffersFromSteamVr)
        {
            return OtherSpace;
        }

        return pick.Id is { } id && steamVrRoomSetups.Contains(id) ? null : NoSetup;
    }

    public static bool HasSetup(IReadOnlyList<RoomSetup> setups, string? universe, bool profileUsesSteamVrLh) =>
        profileUsesSteamVrLh
            ? RoomSetupApplier.InUse(setups, SteamVrRoom.LighthouseOrigin, universe) is not null
            : setups.Any(s => s.Universe is null || string.Equals(s.Universe, universe, StringComparison.Ordinal));

    public static string? FromSteam(string? steamDir, IReadOnlyList<KnownStation> stations, IReadOnlyList<RoomSetup> setups, bool profileUsesSteamVrLh)
    {
        var (universes, roomSetups) = SteamVrRoom.Read(steamDir);
        var pick = CurrentUniverse.Pick(universes, roomSetups, stations);
        return For(pick, roomSetups, HasSetup(setups, pick.Id, profileUsesSteamVrLh), profileUsesSteamVrLh);
    }
}
