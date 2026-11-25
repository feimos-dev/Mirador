namespace Mirador.Core.Monado;

public sealed record MonadoSettings(
    int LighthouseWaitMs = 3000,
    int RenderScalePercent = 100,
    int CompositorMinTimeMs = 6)
{
    public MonadoSettings Clamped() => this with
    {
        LighthouseWaitMs = Snap(LighthouseWaitMs, min: 1000, max: 30000, step: 1000),
        RenderScalePercent = Snap(RenderScalePercent, min: 50, max: 200, step: 5),
        CompositorMinTimeMs = Snap(CompositorMinTimeMs, min: 1, max: 20, step: 1),
    };

    private static int Snap(int value, int min, int max, int step)
    {
        var rounded = (long)Math.Round((double)value / step, MidpointRounding.AwayFromZero) * step;
        return (int)Math.Clamp(rounded, min, max);
    }
}
