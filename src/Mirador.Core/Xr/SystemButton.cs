namespace Mirador.Core.Xr;

/// <summary>What a press of the system button asks for.</summary>
public enum SystemButtonAction
{
    None,

    /// <summary>Pressed and let go: open or close the dashboard.</summary>
    Toggle,

    /// <summary>Held for <see cref="SystemButton.HoldSeconds"/>: put the origin under the head.</summary>
    Recentre,
}

/// <summary>
/// Tells a press of the system button from a hold. A press counts when the button is let go, so that a hold never
/// opens the dashboard on its way to a recentre.
/// </summary>
public sealed class SystemButton
{
    /// <summary>How long the button is held for a recentre.</summary>
    public const double HoldSeconds = 1.0;

    private bool held;
    private double heldSince;
    private bool done;

    /// <summary>Takes the button's state at <paramref name="now"/> (seconds on any steady clock); call it every frame.</summary>
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
            // Held long enough: a recentre now, once a press.
            action = SystemButtonAction.Recentre;
            done = true;
        }
        else if (!down && held && !done && now - heldSince < HoldSeconds)
        {
            // Let go before that: a press. Let go later with no recentre seen (the button wasn't read while it was held,
            // as when its controller went away): nothing.
            action = SystemButtonAction.Toggle;
        }

        held = down;
        return action;
    }
}
