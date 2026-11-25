using StereoKit;

namespace Mirador.Xr;

/// <summary>The controllers' lasers: StereoKit's line interactors, by hand.</summary>
internal static class Lasers
{
    public static Handed? HandOf(InteractorSource source) =>
        (source & InteractorSource.ControllerLeft) != 0 ? Handed.Left
        : (source & InteractorSource.ControllerRight) != 0 ? Handed.Right
        : null;

    /// <summary><paramref name="hand"/>'s laser while its controller is tracked.</summary>
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
