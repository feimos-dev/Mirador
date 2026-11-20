using Microsoft.Extensions.Time.Testing;
using Mirador.Core.Modules;

namespace Mirador.DirectMode.Tests;

public class DirectModeLogParserTests
{
    [Fact]
    public void Without_the_layer_no_display_session_is_waited_for()
    {
        var time = new FakeTimeProvider();
        var parser = new DirectModeLogParser(time, layerUsed: false);

        time.Advance(TimeSpan.FromMinutes(5));
        parser.Tick();

        Assert.Null(parser.Failed);
    }

    [Fact]
    public void No_display_session_within_a_minute_fails()
    {
        var time = new FakeTimeProvider();
        var parser = new DirectModeLogParser(time);
        parser.Feed("[dvl] warn: NVAPI EnableDirectMode for vendor PVR returned -5; still waiting for the display");

        time.Advance(TimeSpan.FromSeconds(59));
        parser.Tick();
        Assert.Null(parser.Failed);
        Assert.Equal(StatusLevel.Info, parser.Line.Level);

        time.Advance(TimeSpan.FromSeconds(2));
        parser.Tick();

        Assert.NotNull(parser.Failed);
        Assert.StartsWith("The headset's display didn't come up within a minute. ", parser.Failed);
        Assert.Contains(" NVAPI EnableDirectMode for vendor PVR returned -5; still waiting for the display.", parser.Failed);
        Assert.DoesNotContain("warn:", parser.Failed);
        Assert.EndsWith(" Check Diagnostics, then start again.", parser.Failed);
        Assert.Equal(new StatusLine("display", "Display", StatusLevel.Error, parser.Failed), parser.Line);
    }

    [Fact]
    public void A_display_session_in_time_never_fails()
    {
        var time = new FakeTimeProvider();
        var parser = new DirectModeLogParser(time);

        time.Advance(TimeSpan.FromSeconds(30));
        parser.Feed("[dvl] info: display session open: Pimax Dream Air, 7104x3840 at 90.000 Hz, display 0x80061086, adapter 00000000:00014f8d");
        time.Advance(TimeSpan.FromMinutes(10));
        parser.Tick();

        Assert.Null(parser.Failed);
        Assert.Equal(StatusLevel.Ok, parser.Line.Level);
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
        var parser = new DirectModeLogParser();

        foreach (var line in run)
        {
            parser.Feed(line);
        }

        Assert.Equal(new StatusLine("display", "Display", StatusLevel.Ok, "Direct mode, 7104×3840 at 90 Hz, 1 missed of 2,790 frames"), parser.Line);
    }

    [Fact]
    public void Before_monado_says_anything()
    {
        Assert.Equal(new StatusLine("display", "Display", StatusLevel.Info, "Waiting for the display"), new DirectModeLogParser().Line);
    }

    [Fact]
    public void The_display_line_before_any_frames()
    {
        var parser = new DirectModeLogParser();

        parser.Feed("[dvl] info: display session open: Pimax Dream Air, 7104x3840 at 72.5 Hz, …");

        Assert.Equal(new StatusLine("display", "Display", StatusLevel.Ok, "Direct mode, 7104×3840 at 72.5 Hz"), parser.Line);
    }

    [Fact]
    public void Frame_totals_use_thousands_separators()
    {
        var parser = new DirectModeLogParser();
        parser.Feed("[dvl] info: display session open: Pimax Dream Air, 7104x3840 at 90.000 Hz, …");

        for (var i = 0; i < 60; i++)
        {
            parser.Feed("[dvl] info: 90 presents in 1.0 s (89.5 Hz), 90 flips (89.5 Hz), 0 missed (0.00 %), …");
        }

        Assert.Equal("Direct mode, 7104×3840 at 90 Hz, 0 missed of 5,400 frames", parser.Line.Text);
    }

    [Fact]
    public void Other_lines_change_nothing()
    {
        var parser = new DirectModeLogParser();

        Assert.False(parser.Feed("INFO [ipc_server] Client connected: hello_xr"));
        Assert.False(parser.Feed("Pimax Dream Air opened."));
        Assert.False(parser.Feed(""));
    }
}
