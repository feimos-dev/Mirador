using Mirador.Core.BaseStations;
using Mirador.Core.Lighthouse;

namespace Mirador.Core.Playspace;

/// <summary>
/// Home's and the Lighthouse page's warning when the Lighthouse space in use has no room setup: neither Mirador's own
/// nor, with Monado's <c>steamvr_lh</c> driver, SteamVR's. Read from the files alone, without starting Monado.
/// </summary>
public static class RoomSetupWarning
{
    public const string NoSetup =
        "No room setup for this space: the floor and forward direction may be wrong in games. Set it up on the Lighthouse page.";

    public const string OtherSpace =
        "SteamVR's room setup of another space is used: the floor and forward direction may be wrong in games. Set it up on the Lighthouse page.";

    public const string SteamVrChanged =
        "SteamVR's room setup changed since Mirador's: the floor and forward direction may be wrong in games. Measure again on the Lighthouse page.";

    /// <summary>
    /// Whether the setup that applies to <c>steamvr_lh</c>'s origin in <paramref name="universe"/> was measured on
    /// another SteamVR room setup than the one <c>steamvr_lh</c> applies now (<paramref name="now"/>; null: no Steam): it
    /// would then apply on top of the new one. A setup that didn't note it, and profiles without <c>steamvr_lh</c>, say
    /// nothing.
    /// </summary>
    public static bool BaseChanged(IReadOnlyList<RoomSetup> setups, string? universe, SteamVrBase? now, bool profileUsesSteamVrLh) =>
        profileUsesSteamVrLh
        && now is not null
        && RoomSetupApplier.InUse(setups, SteamVrRoom.LighthouseOrigin, universe)?.Base is { } taken
        && !taken.SameAs(now);

    /// <summary>
    /// The warning for the current universe <paramref name="pick"/>; null when Mirador has a setup, or when
    /// <c>steamvr_lh</c> is used and applies SteamVR's room setup of this universe. When <c>steamvr_lh</c> applies
    /// another universe's (<see cref="UniversePick.DiffersFromSteamVr"/>), the warning says so.
    /// </summary>
    /// <param name="pick">The universe in use.</param>
    /// <param name="steamVrRoomSetups">The universes with a room setup in SteamVR's <c>chaperone_info.vrchap</c>.</param>
    /// <param name="miradorHasSetup">Mirador keeps a setup for this universe (<see cref="HasSetup"/>).</param>
    /// <param name="profileUsesSteamVrLh">The profile's Monado uses <c>steamvr_lh</c>, which applies SteamVR's room setup.</param>
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

    /// <summary>
    /// Whether Mirador applies a setup in <paramref name="universe"/>: one kept for it, or one taken without a Lighthouse
    /// universe (<see cref="RoomSetupApplier"/>'s fallback); for <c>steamvr_lh</c>'s origin only when the profile uses
    /// it, else for any origin.
    /// </summary>
    public static bool HasSetup(IReadOnlyList<RoomSetup> setups, string? universe, bool profileUsesSteamVrLh) =>
        profileUsesSteamVrLh
            ? RoomSetupApplier.InUse(setups, SteamVrRoom.LighthouseOrigin, universe) is not null
            : setups.Any(s => s.Universe is null || string.Equals(s.Universe, universe, StringComparison.Ordinal));

    /// <summary><see cref="For"/> on Steam's files as they are now (none without Steam) and Mirador's setups.</summary>
    public static string? FromSteam(string? steamDir, IReadOnlyList<KnownStation> stations, IReadOnlyList<RoomSetup> setups, bool profileUsesSteamVrLh)
    {
        var (universes, roomSetups) = SteamVrRoom.Read(steamDir);
        var pick = CurrentUniverse.Pick(universes, roomSetups, stations);
        return For(pick, roomSetups, HasSetup(setups, pick.Id, profileUsesSteamVrLh), profileUsesSteamVrLh);
    }
}
