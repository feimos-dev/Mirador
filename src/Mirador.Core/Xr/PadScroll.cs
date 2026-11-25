namespace Mirador.Core.Xr;

/// <summary>
/// A trackpad as a mouse wheel: while a finger is on it, its movement along the pad turns the wheel in proportion
/// (up scrolls up), in steps of at least <see cref="MinStep"/> so that programs which take every turn as a whole notch
/// don't race.
/// </summary>
public sealed class PadScroll
{
    /// <summary>The wheel's turn, in Windows' units (120 a notch), for the length of the pad (its y runs from -1 to 1).</summary>
    public const float WheelPerPad = 720;

    /// <summary>The smallest turn sent, a quarter of a notch.</summary>
    public const int MinStep = 30;

    private float? last;
    private float pending;

    /// <summary>The finger's place this frame (<paramref name="y"/>, while <paramref name="touching"/>): the turn to send now, 0 for none.</summary>
    public int Update(bool touching, float y)
    {
        if (!touching)
        {
            (last, pending) = (null, 0);
            return 0;
        }

        if (last is { } before)
        {
            pending += (y - before) * WheelPerPad / 2;
        }

        last = y;
        var turn = (int)pending;
        if (Math.Abs(turn) < MinStep)
        {
            return 0;
        }

        pending -= turn;
        return turn;
    }
}
