namespace Mirador.Core.Xr;

/// <summary>
/// How far along a laser a grabbed thing is. The thumbstick pushes it away and pulls it in at a steady speed; and as
/// StereoKit's own grabbing does, the distance also follows the hand's reach from the head. It stays between
/// <see cref="NearestM"/> and <see cref="FarthestM"/>: at either end the grab starts over from there, so moving back
/// the other way works at once.
/// </summary>
public sealed class RayGrab
{
    public const float NearestM = 0.1f;
    public const float FarthestM = 5;

    /// <summary>How fast the thumbstick pushed all the way moves it, in metres a second.</summary>
    public const float PushSpeed = 2;

    /// <summary>A thumbstick pushed less than this pushes nothing.</summary>
    public const float StickDeadZone = 0.15f;

    /// <summary>A hand nearer the head than this counts as this near, so the distance stays finite.</summary>
    private const float NearestReachM = 0.05f;

    private float distance;
    private float reach;

    /// <summary>The grab begins <paramref name="distanceM"/> along the laser, with the hand <paramref name="reachM"/> from the head.</summary>
    public void Start(float distanceM, float reachM) =>
        (distance, reach) = (Math.Clamp(distanceM, NearestM, FarthestM), Math.Max(NearestReachM, reachM));

    /// <summary>
    /// The distance along the laser now, the hand <paramref name="reachM"/> from the head and the thumbstick at
    /// <paramref name="stick"/> (-1 to 1, forward pushes away) for the <paramref name="seconds"/> since the last call.
    /// </summary>
    public float Update(float reachM, float stick, float seconds)
    {
        reachM = Math.Max(NearestReachM, reachM);
        if (Math.Abs(stick) >= StickDeadZone)
        {
            distance += stick * PushSpeed * seconds * reach / reachM; // the same speed however far the hand reaches
        }

        var along = distance * reachM / reach;
        if (along is >= NearestM and <= FarthestM)
        {
            return along;
        }

        along = Math.Clamp(along, NearestM, FarthestM);
        (distance, reach) = (along, reachM);
        return along;
    }
}
