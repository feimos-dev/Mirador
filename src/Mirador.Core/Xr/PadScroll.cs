namespace Mirador.Core.Xr;

public sealed class PadScroll
{
    public const float WheelPerPad = 720;

    public const int MinStep = 30;

    private float? last;
    private float pending;

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
