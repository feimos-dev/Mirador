using Mirador.Ipc;

namespace Mirador.Core.Tests.Xr;

public class DesktopLayerLinkTests
{
    [Fact]
    public void A_placement_reads_back_as_it_was_written()
    {
        var placement = new DesktopPlacement(65537, [0, 0.38268343f, 0, 0.9238795f, 0.1f, 1.62f, -1.5f], 2, 1.125f);

        var line = DesktopLayerLink.FormatShow(placement);
        var read = DesktopLayerLink.ParseShow(line);

        Assert.StartsWith("SHOW 65537 ", line);
        Assert.NotNull(read);
        Assert.Equal(placement.Monitor, read.Monitor);
        Assert.Equal(placement.Pose, read.Pose);
        Assert.Equal((2f, 1.125f), (read.Width, read.Height));
    }

    [Theory]
    [InlineData("HIDE")]
    [InlineData("SHOW")]
    [InlineData("SHOW 1 0 0 0 1 0 0 0 2")]
    [InlineData("SHOW 1 0 0 0 1 0 0 0 2 1 9")]
    [InlineData("SHOW x 0 0 0 1 0 0 0 2 1")]
    [InlineData("SHOW 1 0 0 0 1 0 NaN 0 2 1")]
    [InlineData("SHOW 1 0 0 0 1 0 0 0 0 1")]
    [InlineData("SHOW 1 0 0 0 1 0 0 0 2 -1")]
    public void Anything_but_a_whole_show_line_is_no_placement(string line)
    {
        Assert.Null(DesktopLayerLink.ParseShow(line));
    }

    [Fact]
    public void A_pose_must_be_seven_numbers()
    {
        Assert.Throws<ArgumentException>(() => DesktopLayerLink.FormatShow(new DesktopPlacement(1, [0, 0, 0, 1], 2, 1)));
    }
}
