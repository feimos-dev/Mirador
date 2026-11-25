using System.Runtime.InteropServices;

namespace Mirador.App.Services;

internal sealed class MinimumSize
{
    private const uint WmGetMinMaxInfo = 0x0024;

    private readonly int width;
    private readonly int height;

    private readonly SubclassProc proc;

    public MinimumSize(IntPtr window, int width, int height)
    {
        this.width = width;
        this.height = height;
        proc = OnMessage;

        SetWindowSubclass(window, proc, 1, 0);
    }

    private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        var result = DefSubclassProc(window, message, wParam, lParam);
        if (message != WmGetMinMaxInfo)
        {
            return result;
        }

        var scale = GetDpiForWindow(window) / 96.0;
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MinTrackSize.X = (int)Math.Ceiling(width * scale);
        info.MinTrackSize.Y = (int)Math.Ceiling(height * scale);
        Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
        return IntPtr.Zero;
    }

    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
