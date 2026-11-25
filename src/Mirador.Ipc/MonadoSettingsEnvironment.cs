using System.Globalization;

namespace Mirador.Ipc;

public readonly record struct SettingRange(int Min, int Max, int Step)
{
    public bool Contains(int value) => value >= Min && value <= Max && (value - Min) % Step == 0;
}

public static class MonadoSettingsEnvironment
{
    public const string LighthouseWait = "LH_DISCOVER_WAIT_MS";
    public const string RenderScale = "XRT_COMPOSITOR_SCALE_PERCENTAGE";
    public const string CompositorMinTime = "U_PACING_COMP_MIN_TIME_MS";
    public const string ExitOnDisconnect = "IPC_EXIT_ON_DISCONNECT";
    public const string StandbyOnExit = "LH_STANDBY_ON_EXIT";

    public static readonly SettingRange LighthouseWaitRange = new(1000, 30000, 1000);

    public static readonly SettingRange RenderScaleRange = new(50, 200, 5);

    public static readonly SettingRange CompositorMinTimeRange = new(1, 20, 1);

    public static void Write(
        Action<string, string> set, int lighthouseWaitMs, int renderScalePercent, int compositorMinTimeMs,
        bool standbyOnExit = false)
    {
        set(LighthouseWait, Number(lighthouseWaitMs));
        set(RenderScale, Number(renderScalePercent));
        set(CompositorMinTime, Number(compositorMinTimeMs));
        if (standbyOnExit)
        {
            set(StandbyOnExit, "1");
        }
    }

    public static string? OutOfRange(int lighthouseWaitMs, int renderScalePercent, int compositorMinTimeMs)
    {
        if (!LighthouseWaitRange.Contains(lighthouseWaitMs))
        {
            return LighthouseWait;
        }

        if (!RenderScaleRange.Contains(renderScalePercent))
        {
            return RenderScale;
        }

        if (!CompositorMinTimeRange.Contains(compositorMinTimeMs))
        {
            return CompositorMinTime;
        }

        return null;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
