using Mirador.Core.Modules;

namespace Mirador.Pimax.Tests;

public class DreamAirLogParserTests
{
    private static StatusLine Line(DreamAirLogParser parser, string key) => parser.Lines.Single(l => l.Key == key);

    private static readonly GeometryResult Uploaded = new(true, "XR5DA-P501G0D202652600422", GeometryUploader.Uploaded, "log");

    [Fact]
    public void A_monado_with_no_driver_for_the_headset_fails_at_once_with_that_reason()
    {
        var parser = new DreamAirLogParser();

        parser.Feed(" WARN [p_dev_get_usb_device] No builder can be used to create a head device");

        Assert.Equal(
            "This Monado has no driver for the Pimax Dream Air: none of its drivers took the headset. Use a profile whose Monado has a driver for it.",
            parser.Failed);
        Assert.Equal(parser.Failed, Line(parser, "headset").Text);
    }

    [Fact]
    public void Before_monado_says_anything()
    {
        Assert.Equal([new StatusLine("eye", "Eye tracker", StatusLevel.Ok, "Geometry uploaded")], new DreamAirLogParser(Uploaded).Lines);
        Assert.Null(new DreamAirLogParser(Uploaded).Failed);
    }

    [Fact]
    public void Without_a_geometry_upload_there_is_no_eye_line()
    {
        Assert.Empty(new DreamAirLogParser().Lines);
    }

    [Fact]
    public void A_failed_upload_is_the_eye_line()
    {
        var parser = new DreamAirLogParser(new GeometryResult(false, null, "The eye tracker isn't on USB. Is the headset connected and switched on?", "log"));

        Assert.Equal(
            new StatusLine("eye", "Eye tracker", StatusLevel.Warning, "The eye tracker isn't on USB. Is the headset connected and switched on?"),
            Line(parser, "eye"));
    }

    [Fact]
    public void No_builder_for_the_head_adds_the_headset_line()
    {
        var parser = new DreamAirLogParser(Uploaded, headset: "Pimax Crystal Light");
        Assert.DoesNotContain(parser.Lines, l => l.Key == DreamAirLogParser.HeadsetKey);

        Assert.True(parser.Feed("\tNo builder can be used to create a head device"));

        var headset = Line(parser, DreamAirLogParser.HeadsetKey);
        Assert.Equal(StatusLevel.Error, headset.Level);
        Assert.Contains("no driver for the Pimax Crystal Light", headset.Text);
        Assert.Equal(["eye", "headset"], parser.Lines.Select(l => l.Key));
    }

    [Fact]
    public void Driver_log_lines_change_nothing()
    {
        var parser = new DreamAirLogParser(Uploaded);
        var before = parser.Lines;
        string[] driver =
        [
            "Pimax Dream Air opened.",
            "Eye tracker connected.",
            "Eye calibration applied (from file).",
            "Gaze correction loaded from x",
            "Lost the headset, the panels stay dark until Monado restarts.",
            "Replaced the saved calibration in x",
            "Lost the eye tracker (x), reconnecting.",
            "[dvl] info: display session open: Pimax Dream Air, 7104x3840 at 90.000 Hz, …",
        ];

        foreach (var line in driver)
        {
            Assert.False(parser.Feed(line), line);
        }

        Assert.Equal(before, parser.Lines);
    }

    [Fact]
    public void Other_lines_change_nothing()
    {
        var parser = new DreamAirLogParser();

        Assert.False(parser.Feed("INFO [ipc_server] Client connected: hello_xr"));
        Assert.False(parser.Feed(""));
    }
}
