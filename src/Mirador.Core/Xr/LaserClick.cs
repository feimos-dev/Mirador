namespace Mirador.Core.Xr;

public sealed class LaserClick
{
    public const float HoldM = 0.04f;

    public const float RestTrigger = 0.1f;

    private (float U, float V)? rest;
    private (float U, float V)? held;
    private bool following;

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

    public void Left()
    {
        rest = null;
        held = null;
        following = false;
    }
}
