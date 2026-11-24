using System.Runtime.InteropServices;
using Mirador.Core.Xr;

namespace Mirador.Xr;

/// <summary>
/// The Windows desktop as the dashboard's laser reaches it: its monitors, and the mouse moved, pressed and scrolled as
/// if by hand (<c>SendInput</c>). Windows doesn't pass such input to a window of a program running as administrator.
/// </summary>
internal static class DesktopMouse
{
    private const uint Move = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004, Wheel = 0x0800, VirtualDesk = 0x4000, Absolute = 0x8000;
    private const int WheelNotch = 120;

    /// <summary>
    /// Makes this process see the desktop in real pixels, whatever each monitor's scaling: call it once, before
    /// anything else here, or the laser lands beside the pointer on a scaled monitor.
    /// </summary>
    public static void UseRealPixels() => SetProcessDpiAwarenessContext(new IntPtr(-4)); // per monitor, version 2

    /// <summary>The monitors of the desktop, the primary one first, then from left to right.</summary>
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

    /// <summary>Puts the pointer at (<paramref name="u"/>, <paramref name="v"/>) of <paramref name="monitor"/>, from its top left corner.</summary>
    public static void MoveTo(DesktopMonitor monitor, float u, float v)
    {
        var all = new DesktopRect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
        var (x, y) = DesktopMaths.Absolute(u, v, monitor.Bounds, all);
        Send(new MouseInput { X = x, Y = y, Flags = Move | Absolute | VirtualDesk });
    }

    /// <summary>Presses or lets go of the left button where the pointer is.</summary>
    public static void Left(bool down) => Send(new MouseInput { Flags = down ? LeftDown : LeftUp });

    /// <summary>Turns the wheel by <paramref name="notches"/>: positive away from the user, which scrolls up.</summary>
    public static void Scroll(int notches)
    {
        if (notches != 0)
        {
            Send(new MouseInput { Data = unchecked((uint)(notches * WheelNotch)), Flags = Wheel });
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

    /// <summary><c>INPUT</c> for a mouse: on 64-bit Windows its union begins at byte 8, and the mouse's is its largest member.</summary>
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
