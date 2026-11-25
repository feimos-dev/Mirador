using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Mirador.Core.Modules;

public readonly record struct UsbId(ushort Vid, ushort Pid);

public interface IUsbDevices
{
    IReadOnlySet<UsbId> Present();
}

public sealed partial class WmiUsbDevices : IUsbDevices
{
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(5);

    public IReadOnlySet<UsbId> Present()
    {
        try
        {
            return Parse(QueryDeviceIds());
        }
        catch (ManagementException e) when (e.ErrorCode == ManagementStatus.Timedout)
        {
            throw new MiradorException(
                $"Windows didn't list the USB devices within {WmiTimeout.TotalSeconds:0} seconds, so Mirador can't tell which headset is plugged in. Try again.");
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException)
        {
            throw new MiradorException(
                $"Windows couldn't list the USB devices ({e.Message.Trim().TrimEnd('.')}), so Mirador can't tell which headset is plugged in. Try again.");
        }
    }

    internal static IReadOnlySet<UsbId> Parse(IEnumerable<string?> deviceIds)
    {
        var found = new HashSet<UsbId>();
        foreach (var id in deviceIds)
        {
            if (id is not null && UsbDeviceId().Match(id) is { Success: true } match)
            {
                found.Add(new UsbId(Hex(match.Groups["vid"].Value), Hex(match.Groups["pid"].Value)));
            }
        }

        return found;
    }

    private static ushort Hex(string digits) => ushort.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    private static List<string?> QueryDeviceIds()
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\cimv2",
            @"SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\VID_%'",
            new System.Management.EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = true });
        using var results = searcher.Get();
        var ids = new List<string?>();
        foreach (var item in results)
        {
            using (item)
            {
                ids.Add(item["DeviceID"] as string);
            }
        }

        return ids;
    }

    [GeneratedRegex(@"^USB\\VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsbDeviceId();
}
