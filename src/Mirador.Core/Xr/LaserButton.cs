namespace Mirador.Core.Xr;

public sealed class LaserButton
{
    public const float PressTrigger = 0.75f;
    public const float ReleaseTrigger = 0.35f;

    private float last;

    public bool Down { get; private set; }

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
