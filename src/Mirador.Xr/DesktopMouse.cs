using System.Runtime.InteropServices;
using Mirador.Core.Xr;

namespace Mirador.Xr;

internal static class DesktopMouse
{
    private const uint Move = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004, Wheel = 0x0800, VirtualDesk = 0x4000, Absolute = 0x8000;
    private const int WheelNotch = 120;

    public static void UseRealPixels() => SetProcessDpiAwarenessContext(new IntPtr(-4));

    public static IReadOnlyList<DesktopMonitor> Monitors()
    {
        var found = new List<DesktopMonitor>();
        MonitorEnumProc each = (IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                var r = info.Monitor;
                found.Add(new DesktopMonitor(monitor, new DesktopRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), (info.Flags & 1) != 0));
            }

            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, each, IntPtr.Zero);
        GC.KeepAlive(each);
        return [.. found.OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top)];
    }

    public static void MoveTo(DesktopMonitor monitor, float u, float v)
    {
        var all = new DesktopRect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
        var (x, y) = DesktopMaths.Absolute(u, v, monitor.Bounds, all);
        Send(new MouseInput { X = x, Y = y, Flags = Move | Absolute | VirtualDesk });
    }

    public static void Left(bool down) => Send(new MouseInput { Flags = down ? LeftDown : LeftUp });

    public static void Scroll(int notches) => Turn(notches * WheelNotch);

    public static void Turn(int units)
    {
        if (units != 0)
        {
            Send(new MouseInput { Data = unchecked((uint)units), Flags = Wheel });
        }
    }

    private static void Send(MouseInput mouse)
    {
        var input = new Input { Type = 0, Mouse = mouse };
        SendInput(1, ref input, Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Input
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint SendInput(uint count, ref Input input, int size);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc each, IntPtr data);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}
