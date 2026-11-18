using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Playspace;

/// <summary>Puts Mirador's room setups into a running Monado through libmonado.</summary>
public static class RoomSetupApplier
{
    /// <summary>
    /// Sets the offset of each of Monado's tracking origins that has a setup in <paramref name="store"/> for
    /// <paramref name="universe"/>, found by the origin's name. Origins without one are left as they are. Nothing is set
    /// when this libmonado doesn't move tracking origins.
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
        var setups = store.All().Where(s => string.Equals(s.Universe, universe, StringComparison.Ordinal)).ToList();
        var applied = 0;
        for (var i = 0; i < origins.Count; i++)
        {
            if (setups.Find(s => string.Equals(s.OriginName, origins[i], StringComparison.Ordinal)) is { } setup)
            {
                client.SetOriginOffset((uint)i, setup.Offset);
                applied++;
            }
        }

        return applied;
    }
}
