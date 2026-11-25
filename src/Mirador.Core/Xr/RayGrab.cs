namespace Mirador.Core.Xr;

public sealed class RayGrab
{
    public const float NearestM = 0.1f;
    public const float FarthestM = 5;

    public const float PushSpeed = 2;

    public const float StickDeadZone = 0.15f;

    private const float NearestReachM = 0.05f;

    private float distance;
    private float reach;

    public void Start(float distanceM, float reachM) =>
        (distance, reach) = (Math.Clamp(distanceM, NearestM, FarthestM), Math.Max(NearestReachM, reachM));

    public float Update(float reachM, float stick, float seconds)
    {
        reachM = Math.Max(NearestReachM, reachM);
        if (Math.Abs(stick) >= StickDeadZone)
        {
            distance += stick * PushSpeed * seconds * reach / reachM;
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
