using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Playspace;

/// <summary>Puts Mirador's room setups into a running Monado through libmonado.</summary>
public static class RoomSetupApplier
{
    /// <summary>
    /// Sets the offset of each of Monado's tracking origins that has a setup in <paramref name="store"/>, found by the
    /// origin's name: the one for <paramref name="universe"/>, else the one taken without a Lighthouse universe. Origins
    /// without either are left as they are. Nothing is set when this libmonado doesn't move tracking origins.
    /// </summary>
    /// <returns>How many origins were set.</returns>
    /// <exception cref="MiradorException">Monado didn't take an offset (the origins before it were set).</exception>
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
            var setup = setups.FirstOrDefault(s => Is(s, name, universe)) ?? setups.FirstOrDefault(s => Is(s, name, null));
            if (setup is not null)
            {
                client.SetOriginOffset((uint)i, setup.Offset);
                applied++;
            }
        }

        return applied;
    }

    private static bool Is(RoomSetup setup, string originName, string? universe) =>
        string.Equals(setup.OriginName, originName, StringComparison.Ordinal)
        && string.Equals(setup.Universe, universe, StringComparison.Ordinal);
}
