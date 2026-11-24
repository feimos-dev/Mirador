namespace Mirador.Core.Xr;

/// <summary>
/// Where a laser's mouse pointer goes around a trigger press. Pulling a trigger turns the controller a little, which at
/// the panel's distance moves the laser centimetres, and Windows takes a press and release that far apart as a drag. So
/// from the moment the trigger leaves its rest, the pointer stays where the laser was just before, until the laser goes
/// further than <see cref="HoldM"/> from there: then it follows again, and a press held is a drag.
/// </summary>
public sealed class LaserClick
{
    /// <summary>How far the laser may wander on the panel, in metres, while the pointer holds still.</summary>
    public const float HoldM = 0.04f;

    /// <summary>A trigger pulled no further than this is at rest.</summary>
    public const float RestTrigger = 0.1f;

    private (float U, float V)? rest;
    private (float U, float V)? held;
    private bool following;

    /// <summary>
    /// Where the pointer goes this frame, as fractions of the panel from its top left corner, with the laser at
    /// <paramref name="at"/>, the trigger pulled <paramref name="trigger"/> (0 to 1) and the left button down or not.
    /// </summary>
    public (float U, float V) Pointer((float U, float V) at, float trigger, bool pressed, float widthM, float heightM)
    {
        if (!pressed && trigger <= RestTrigger)
        {
            rest = at;
            held = null;
            following = false;
            return at;
        }

        if (following)
        {
            return at;
        }

        held ??= rest ?? at;
        var du = (at.U - held.Value.U) * widthM;
        var dv = (at.V - held.Value.V) * heightM;
        if ((du * du) + (dv * dv) > HoldM * HoldM)
        {
            following = true;
            return at;
        }

        return held.Value;
    }

    /// <summary>The laser left the panel: the next time it comes onto it starts afresh.</summary>
    public void Left()
    {
        rest = null;
        held = null;
        following = false;
    }
}
