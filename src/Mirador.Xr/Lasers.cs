using StereoKit;

namespace Mirador.Xr;

internal static class Lasers
{
    public static Handed? HandOf(InteractorSource source) =>
        (source & InteractorSource.ControllerLeft) != 0 ? Handed.Left
        : (source & InteractorSource.ControllerRight) != 0 ? Handed.Right
        : null;

    public static Interactor? Of(Handed hand)
    {
        foreach (var laser in Interactor.All)
        {
            if (laser.Type == InteractorType.Line && laser.Tracked.IsActive() && HandOf(laser.Source) == hand)
            {
                return laser;
            }
        }

        return null;
    }
}
