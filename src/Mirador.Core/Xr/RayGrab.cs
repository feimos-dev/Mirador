namespace Mirador.Core.Xr;

/// <summary>
/// How far along a laser a grabbed thing is: in proportion to the hand's reach from the head, as StereoKit's own
/// grabbing does, so pushing the hand out pushes it away; never beyond <see cref="FarthestM"/>. Pushed past that, it
/// stays there and the grab starts over from it, so pulling back brings it closer at once.
/// </summary>
public sealed class RayGrab
{
    public const float FarthestM = 5;

    /// <summary>A hand nearer the head than this counts as this near, so the distance stays finite.</summary>
    private const float NearestReachM = 0.05f;

    private float distance;
    private float reach;

    /// <summary>The grab begins <paramref name="distanceM"/> along the laser, with the hand <paramref name="reachM"/> from the head.</summary>
    public void Start(float distanceM, float reachM) => (distance, reach) = (distanceM, Math.Max(NearestReachM, reachM));

    /// <summary>The distance along the laser with the hand now <paramref name="reachM"/> from the head.</summary>
    public float Update(float reachM)
    {
        reachM = Math.Max(NearestReachM, reachM);
        var along = distance * reachM / reach;
        if (along <= FarthestM)
        {
            return along;
        }

        (distance, reach) = (FarthestM, reachM);
        return FarthestM;
    }
}
