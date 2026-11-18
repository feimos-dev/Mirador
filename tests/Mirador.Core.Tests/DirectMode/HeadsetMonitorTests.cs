using Mirador.Core.DirectMode;

namespace Mirador.Core.Tests.DirectMode;

public class HeadsetMonitorTests
{
    private const string PimaxId = @"\?\DISPLAY#PVR1019#5&abc&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string DellId = @"\?\DISPLAY#DEL4321#5&abc&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    [Fact]
    public void A_pimax_monitor_on_the_desktop_is_found()
    {
        var monitors = new[] { new MonitorEntry(DellId, true), new MonitorEntry(PimaxId, true) };

        Assert.True(HeadsetMonitor.HeadsetOnDesktop(monitors));
    }

    [Fact]
    public void A_pimax_monitor_off_the_desktop_is_not()
    {
        var monitors = new[] { new MonitorEntry(DellId, true), new MonitorEntry(PimaxId, false) };

        Assert.False(HeadsetMonitor.HeadsetOnDesktop(monitors));
    }

    [Fact]
    public void Other_monitors_are_not()
    {
        var monitors = new[] { new MonitorEntry(DellId, true) };

        Assert.False(HeadsetMonitor.HeadsetOnDesktop(monitors));
        Assert.False(HeadsetMonitor.HeadsetOnDesktop([]));
    }

    [Fact]
    public void The_vendor_is_matched_without_regard_to_case_and_after_a_backslash_too()
    {
        Assert.True(HeadsetMonitor.HeadsetOnDesktop([new MonitorEntry(@"monitor\pvr1019\0001", true)]));
        Assert.True(HeadsetMonitor.HeadsetOnDesktop([new MonitorEntry(@"\?\display#pvr1019#1", true)]));
        Assert.True(HeadsetMonitor.HeadsetOnDesktop([new MonitorEntry(DellId, true)], "DEL"));
    }

    [Fact]
    public void The_vendor_must_start_a_part_of_the_id()
    {
        Assert.False(HeadsetMonitor.HeadsetOnDesktop([new MonitorEntry(@"\?\DISPLAY#XPVR1#1", true)]));
    }

    [MachineFact]
    public void List_returns_at_least_one_monitor()
    {
        Assert.NotEmpty(HeadsetMonitor.List());
    }
}
