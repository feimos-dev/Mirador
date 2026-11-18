using Microsoft.Extensions.Time.Testing;
using Mirador.Core.Modules;

namespace Mirador.Pimax.Tests;

public class DreamAirLogParserTests
{
    private static StatusLine Line(DreamAirLogParser parser, string key) => parser.Lines.Single(l => l.Key == key);

    private static readonly GeometryResult Uploaded = new(true, "XR5DA-P501G0D202652600422", GeometryUploader.Uploaded, "log");

    [Fact]
    public void No_display_session_within_a_minute_fails()
    {
        var time = new FakeTimeProvider();
        var parser = new DreamAirLogParser(timeProvider: time);
        parser.Feed("[dvl] warning: NVAPI EnableDirectMode for vendor PVR returned -5; still waiting for the display");

        time.Advance(TimeSpan.FromSeconds(59));
        parser.Tick();
        Assert.Null(parser.Failed);
        Assert.Equal(StatusLevel.Info, Line(parser, "display").Level);

        time.Advance(TimeSpan.FromSeconds(2));
        parser.Tick();

        Assert.NotNull(parser.Failed);
        Assert.StartsWith("The headset's display didn't come up within a minute. ", parser.Failed);
        Assert.Contains("still waiting for the display", parser.Failed);
        Assert.EndsWith(" Check Diagnostics, then start again.", parser.Failed);
        var display = Line(parser, "display");
        Assert.Equal(StatusLevel.Error, display.Level);
        Assert.Equal(parser.Failed, display.Text);
    }

    [Fact]
    public void A_display_session_in_time_never_fails()
    {
        var time = new FakeTimeProvider();
        var parser = new DreamAirLogParser(timeProvider: time);

        time.Advance(TimeSpan.FromSeconds(30));
        parser.Feed("[dvl] info: display session open: Pimax Dream Air, 7104x3840 at 90.000 Hz, display 0x80061086, adapter 00000000:00014f8d");
        time.Advance(TimeSpan.FromMinutes(10));
        parser.Tick();

        Assert.Null(parser.Failed);
        Assert.Equal(StatusLevel.Ok, Line(parser, "display").Level);
    }

    [Fact]
    public void Log_parser_reads_the_layers_lines()
    {
        // The layer's lines of a real run, in their order.
        string[] run =
        [
            "[dvl] info: display session open: Pimax Dream Air, 7104x3840 at 90.000 Hz, display 0x80061086, adapter 00000000:00014f8d",
            "[dvl] info: 90 presents in 1.0 s (89.6 Hz), 90 flips (89.6 Hz), 0 missed (0.00 %), 0 link drops; dropped 0, failed 0, unconfirmed 0, reserved 0, held 0, target skips 0; event wake median 0.05 ms, max 0.13 ms",
            "[dvl] info: 2700 presents in 30.0 s (90.0 Hz), 2701 flips (90.0 Hz), 1 missed (0.04 %), 0 link drops; dropped 0, failed 0, unconfirmed 0, reserved 0, held 0, target skips 0; event wake median 0.04 ms, max 0.86 ms",
        ];
        var parser = new DreamAirLogParser(Uploaded);

        foreach (var line in run)
        {
            parser.Feed(line);
        }

        Assert.Equal(
            new StatusLine[]
            {
                new("display", "Display", StatusLevel.Ok, "Direct mode, 7104×3840 at 90 Hz, 1 missed of 2,790 frames"),
                new("eye", "Eye tracker", StatusLevel.Ok, "Geometry uploaded"),
            },
            parser.Lines);
    }

    [Fact]
    public void Before_monado_says_anything()
    {
        Assert.Equal(
            new StatusLine[]
            {
                new("display", "Display", StatusLevel.Info, "Waiting for the display"),
                new("eye", "Eye tracker", StatusLevel.Ok, "Geometry uploaded"),
            },
            new DreamAirLogParser(Uploaded).Lines);
    }

    [Fact]
    public void Without_a_geometry_upload_there_is_no_eye_line()
    {
        Assert.Equal(
            [new StatusLine("display", "Display", StatusLevel.Info, "Waiting for the display")],
            new DreamAirLogParser().Lines);
    }

    [Fact]
    public void The_display_line_before_any_frames()
    {
        var parser = new DreamAirLogParser();

        parser.Feed("[dvl] info: display session open: Pimax Dream Air, 7104x3840 at 72.5 Hz, …");

        Assert.Equal(new StatusLine("display", "Display", StatusLevel.Ok, "Direct mode, 7104×3840 at 72.5 Hz"), Line(parser, "display"));
    }

    [Fact]
    public void Frame_totals_use_thousands_separators()
    {
        var parser = new DreamAirLogParser();
        parser.Feed("[dvl] info: display session open: Pimax Dream Air, 7104x3840 at 90.000 Hz, …");

        for (var i = 0; i < 60; i++)
        {
            parser.Feed("[dvl] info: 90 presents in 1.0 s (89.5 Hz), 90 flips (89.5 Hz), 0 missed (0.00 %), …");
        }

        Assert.Equal("Direct mode, 7104×3840 at 90 Hz, 0 missed of 5,400 frames", Line(parser, "display").Text);
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
        var parser = new DreamAirLogParser(headset: "Pimax Crystal Light");
        Assert.DoesNotContain(parser.Lines, l => l.Key == DreamAirLogParser.HeadsetKey);

        Assert.True(parser.Feed("\tNo builder can be used to create a head device"));

        var headset = Line(parser, DreamAirLogParser.HeadsetKey);
        Assert.Equal(StatusLevel.Error, headset.Level);
        Assert.Contains("no driver for the Pimax Crystal Light", headset.Text);
        Assert.Equal(["display", "headset"], parser.Lines.Select(l => l.Key));
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
