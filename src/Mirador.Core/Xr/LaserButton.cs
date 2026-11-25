namespace Mirador.Core.Xr;

/// <summary>
/// The left button a laser's trigger holds on the desktop: down once the trigger is pulled past
/// <see cref="PressTrigger"/> with the laser on the panel, up only once it is let go below <see cref="ReleaseTrigger"/>,
/// wherever the laser is by then, so a drag lasts as long as the trigger is held.
/// </summary>
public sealed class LaserButton
{
    public const float PressTrigger = 0.75f;
    public const float ReleaseTrigger = 0.35f;

    private float last;

    public bool Down { get; private set; }

    /// <summary>
    /// The trigger of the hand that holds the button (or, while it is up, of the hand whose laser is on the panel) is at
    /// <paramref name="trigger"/>: true when the button went down or up, which <see cref="Down"/> then says. A trigger
    /// already pulled as the laser comes onto the panel presses nothing.
    /// </summary>
    public bool Update(float trigger, bool onPanel)
    {
        var crossed = last < PressTrigger && trigger >= PressTrigger;
        last = trigger;
        if (!Down && onPanel && crossed)
        {
            Down = true;
            return true;
        }

        if (Down && trigger < ReleaseTrigger)
        {
            Down = false;
            return true;
        }

        return false;
    }
}
