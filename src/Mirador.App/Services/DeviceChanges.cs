using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Mirador.App.Services;

internal sealed class DeviceChanges
{
    private const uint WmDeviceChange = 0x0219;
    private const nint DbtDevNodesChanged = 0x0007;

    private readonly DispatcherQueueTimer quiet;

    private readonly SubclassProc proc;

    public DeviceChanges(IntPtr window)
    {
        quiet = DispatcherQueue.GetForCurrentThread().CreateTimer();
        quiet.Interval = TimeSpan.FromSeconds(1);
        quiet.IsRepeating = false;
        quiet.Tick += (_, _) => Changed?.Invoke();
        proc = OnMessage;

        SetWindowSubclass(window, proc, 2, 0);
    }

    public event Action? Changed;

    private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == WmDeviceChange && wParam == DbtDevNodesChanged)
        {
            quiet.Stop();
            quiet.Start();
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
