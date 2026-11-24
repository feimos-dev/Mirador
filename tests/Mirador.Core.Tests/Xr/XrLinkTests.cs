using System.Collections.Concurrent;
using Mirador.Ipc;

namespace Mirador.Core.Tests.Xr;

public class XrLinkTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static string UniqueName() => "mirador-xr-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void A_line_splits_into_its_word_and_the_rest()
    {
        Assert.Equal(("OPEN", "1"), XrLink.Split("OPEN 1"));
        Assert.Equal(("TOGGLE", ""), XrLink.Split("TOGGLE"));
        Assert.Equal(("RECENTRE", "0 0 0 1 1 2 3"), XrLink.Split("RECENTRE 0 0 0 1 1 2 3"));
    }

    [Fact]
    public void A_flag_is_one_or_zero_and_nothing_else()
    {
        Assert.Equal("SHOWN 1", XrLink.Flag(XrLink.Shown, true));
        Assert.True(XrLink.ParseFlag("1"));
        Assert.False(XrLink.ParseFlag("0"));
        Assert.Null(XrLink.ParseFlag("yes"));
    }

    [Fact]
    public void A_recentre_line_carries_the_head_pose_whatever_the_culture()
    {
        var before = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var line = XrLink.FormatRecentre([0, 0.5f, 0, 0.8660254f, -1.25f, 1.7f, 2]);

            Assert.Equal("RECENTRE 0 0.5 0 0.8660254 -1.25 1.7 2", line);
            Assert.Equal([0, 0.5f, 0, 0.8660254f, -1.25f, 1.7f, 2], XrLink.ParsePose(XrLink.Split(line).Value)!);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("0 0 0 1 1 2")]
    [InlineData("0 0 0 1 1 2 x")]
    [InlineData("0 0 0 1 1 2 NaN")]
    [InlineData("")]
    public void A_pose_that_is_not_seven_numbers_is_refused(string rest) => Assert.Null(XrLink.ParsePose(rest));

    [Fact]
    public void Devices_go_into_one_line_and_come_back()
    {
        XrDevice[] devices = [new("L", 82, true), new("R", 64, false), new("4F2A", null, false)];

        var line = XrLink.FormatDevices(devices);

        Assert.Equal("DEVICES L,82,1|R,64,0|4F2A,,0", line);
        Assert.Equal(devices, XrLink.ParseDevices(XrLink.Split(line).Value));
    }

    [Fact]
    public void No_devices_is_the_word_alone()
    {
        Assert.Equal("DEVICES", XrLink.FormatDevices([]));
        Assert.Empty(XrLink.ParseDevices(""));
    }

    [Fact]
    public void A_label_cannot_break_the_line_and_the_line_always_fits()
    {
        var many = Enumerable.Range(0, 40).Select(i => new XrDevice($"a|b,c d{i:D12}", 100, true));

        var line = XrLink.FormatDevices(many);

        Assert.True(line.Length <= AppPipe.MaxLineBytes);
        var back = XrLink.ParseDevices(XrLink.Split(line).Value);
        Assert.Equal(XrLink.MaxDevices, back.Count);
        Assert.All(back, d => Assert.Equal("abcd0000", d.Label));
    }

    [Fact]
    public void A_broken_device_entry_is_left_out()
    {
        Assert.Equal([new XrDevice("R", 5, false)], XrLink.ParseDevices("L,900,0|R,5,0|,1,1|X"));
    }

    [Fact]
    public void What_the_exit_button_ends_goes_as_a_word()
    {
        Assert.Equal("EXITS game", XrLink.FormatExits(XrExit.Game));
        Assert.Equal(XrExit.Monado, XrLink.ParseExits("monado"));
        Assert.Equal(XrExit.Nothing, XrLink.ParseExits("something else"));
    }

    [Fact]
    public async Task A_program_connects_says_which_it_is_and_lines_go_both_ways()
    {
        var name = UniqueName();
        var fromProgram = new BlockingCollection<string>();
        var fromMirador = new BlockingCollection<string>();
        var connected = new TaskCompletionSource<XrLinkConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new XrLinkServer(name);
        server.Connected += connection =>
        {
            connection.Line += fromProgram.Add;
            connected.TrySetResult(connection);
        };
        server.Start();

        using var program = XrLinkConnection.Connect(name, XrLink.DashboardRole, Limit);
        Assert.NotNull(program);
        program.Line += fromMirador.Add;
        program.Start();
        var atMirador = await connected.Task.WaitAsync(Limit);
        program.Send(XrLink.Toggle);
        atMirador.Send("OPEN 1");

        Assert.Equal(XrLink.DashboardRole, atMirador.Role);
        Assert.True(fromProgram.TryTake(out var up, Limit));
        Assert.Equal(XrLink.Toggle, up);
        Assert.True(fromMirador.TryTake(out var down, Limit));
        Assert.Equal("OPEN 1", down);
    }

    [Fact]
    public async Task A_program_that_goes_away_closes_miradors_end()
    {
        var name = UniqueName();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new XrLinkServer(name);
        server.Connected += connection => connection.Closed += () => closed.TrySetResult();
        server.Start();

        var program = XrLinkConnection.Connect(name, XrLink.HomeRole, Limit);
        Assert.NotNull(program);
        program.Start();
        program.Dispose();

        await closed.Task.WaitAsync(Limit);
    }

    [Fact]
    public async Task A_caller_that_is_not_allowed_or_says_nothing_known_gets_no_connection()
    {
        var name = UniqueName();
        var connections = 0;
        await using var server = new XrLinkServer(name, (_, role) => role == XrLink.HomeRole);
        server.Connected += _ => Interlocked.Increment(ref connections);
        server.Start();

        foreach (var role in new[] { XrLink.DashboardRole, "keyboard" })
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var refused = XrLinkConnection.Connect(name, role, Limit);
            Assert.NotNull(refused);
            refused.Closed += () => closed.TrySetResult();
            refused.Start();
            await closed.Task.WaitAsync(Limit);
        }

        Assert.Equal(0, connections);
    }

    [Fact]
    public async Task Stopping_the_server_closes_the_programs_end()
    {
        var name = UniqueName();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new XrLinkServer(name);
        server.Connected += _ => connected.TrySetResult();
        server.Start();
        using var program = XrLinkConnection.Connect(name, XrLink.HomeRole, Limit);
        Assert.NotNull(program);
        program.Closed += () => closed.TrySetResult();
        program.Start();
        await connected.Task.WaitAsync(Limit);

        await server.DisposeAsync().AsTask().WaitAsync(Limit);

        await closed.Task.WaitAsync(Limit);
    }

    [Fact]
    public void Without_a_mirador_listening_there_is_no_connection()
    {
        Assert.Null(XrLinkConnection.Connect(UniqueName(), XrLink.HomeRole, TimeSpan.FromMilliseconds(200)));
    }
}
