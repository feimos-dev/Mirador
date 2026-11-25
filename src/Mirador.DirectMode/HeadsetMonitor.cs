using System.Runtime.InteropServices;

namespace Mirador.DirectMode;

public sealed record MonitorEntry(string DeviceId, bool OnDesktop);

public static class HeadsetMonitor
{
    private const uint EddGetDeviceInterfaceName = 0x1;
    private const uint AttachedToDesktop = 0x1;

    private const int Limit = 64;

    public static bool HeadsetOnDesktop(IReadOnlyList<MonitorEntry> monitors, string pnpVendor = "PVR")
    {
        return monitors.Any(monitor =>
            monitor.OnDesktop
            && (monitor.DeviceId.Contains("#" + pnpVendor, StringComparison.OrdinalIgnoreCase)
                || monitor.DeviceId.Contains(@"\" + pnpVendor, StringComparison.OrdinalIgnoreCase)));
    }

    public static IReadOnlyList<MonitorEntry> List()
    {
        var monitors = new List<MonitorEntry>();
        for (uint adapterIndex = 0; adapterIndex < Limit; adapterIndex++)
        {
            var adapter = NewDevice();
            if (!EnumDisplayDevicesW(null, adapterIndex, ref adapter, 0))
            {
                break;
            }

            var onDesktop = (adapter.StateFlags & AttachedToDesktop) != 0;
            for (uint monitorIndex = 0; monitorIndex < Limit; monitorIndex++)
            {
                var monitor = NewDevice();
                if (!EnumDisplayDevicesW(adapter.DeviceName, monitorIndex, ref monitor, EddGetDeviceInterfaceName))
                {
                    break;
                }

                monitors.Add(new MonitorEntry(monitor.DeviceId, onDesktop));
            }
        }

        return monitors;
    }

    private static DisplayDevice NewDevice() => new() { Size = Marshal.SizeOf<DisplayDevice>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? device, uint devNum, ref DisplayDevice displayDevice, uint flags);
}
