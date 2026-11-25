using Mirador.Core.BaseStations;
using Mirador.Core.Lighthouse;
using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Playspace;

public static class RoomSetupApplier
{
    public static int Apply(IMonadoClient client, RoomSetupStore store, string? universe)
    {
        if (!client.OffersOriginOffsets)
        {
            return 0;
        }

        var origins = client.TrackingOrigins();
        var setups = store.All();
        var applied = 0;
        for (var i = 0; i < origins.Count; i++)
        {
            var name = origins[i];
            var setup = InUse(setups, name, universe);
            if (setup is not null)
            {
                client.SetOriginOffset((uint)i, setup.Offset);
                applied++;
            }
        }

        return applied;
    }

    public static int ApplyWithSteamVr(IMonadoClient client, RoomSetupStore store, string? steamDir, IReadOnlyList<KnownStation> stations) =>
        Apply(client, store, SteamVrNow.Read(steamDir, stations).Pick.Id);

    public static RoomSetup? InUse(IReadOnlyList<RoomSetup> setups, string originName, string? universe) =>
        setups.FirstOrDefault(s => Is(s, originName, universe)) ?? setups.FirstOrDefault(s => Is(s, originName, null));

    private static bool Is(RoomSetup setup, string originName, string? universe) =>
        string.Equals(setup.OriginName, originName, StringComparison.Ordinal)
        && string.Equals(setup.Universe, universe, StringComparison.Ordinal);
}
