namespace Mirador.Core.Xr;

public enum SystemButtonAction
{
    None,

    Toggle,

    Recentre,
}

public sealed class SystemButton
{
    public const double HoldSeconds = 1.0;

    private bool held;
    private double heldSince;
    private bool done;

    public SystemButtonAction Update(bool down, double now)
    {
        var action = SystemButtonAction.None;
        if (down && !held)
        {
            heldSince = now;
            done = false;
        }

        if (down && !done && now - heldSince >= HoldSeconds)
        {
            action = SystemButtonAction.Recentre;
            done = true;
        }
        else if (!down && held && !done && now - heldSince < HoldSeconds)
        {
            action = SystemButtonAction.Toggle;
        }

        held = down;
        return action;
    }
}
