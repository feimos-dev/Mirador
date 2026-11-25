using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Mirador.Core.Monado;

namespace Mirador.Core.Tests.Monado;

public class RunningProcessTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static Process StartPing() =>
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        })!;

    private static RunningProcess Wrap(Process started, Action<Process> terminate) =>
        new(started, new SafeFileHandle(IntPtr.Zero, ownsHandle: false), terminate);

    private static void KillThenFailForTheRestOfTheTree(Process p)
    {
        p.Kill();
        p.WaitForExit();
        throw new AggregateException(new Win32Exception(5));
    }

    [Fact]
    public async Task A_process_that_outlives_dispose_still_reports_its_exit()
    {
        var started = StartPing();
        var process = Wrap(started, _ => throw new AggregateException(new Win32Exception(5)));

        await process.DisposeAsync();
        Assert.False(process.Exited.IsCompleted);
        Process.GetProcessById(started.Id).Kill();

        await process.Exited.WaitAsync(Limit);
    }

    [Fact]
    public async Task Kill_is_a_no_op_when_the_process_is_gone_even_if_part_of_its_tree_could_not_be_ended()
    {
        var process = Wrap(StartPing(), KillThenFailForTheRestOfTheTree);

        process.Kill();

        await process.Exited.WaitAsync(Limit);
        await process.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_does_not_throw_when_ending_the_tree_fails_and_still_releases_the_process()
    {
        var calls = 0;
        var process = Wrap(StartPing(), p =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new AggregateException(new Win32Exception(5));
            }

            KillThenFailForTheRestOfTheTree(p);
        });

        Assert.Throws<AggregateException>(() => process.Kill());

        await process.DisposeAsync();

        Assert.True(process.Exited.IsCompleted);
    }

    [Fact]
    public async Task Dispose_waits_for_a_kill_that_is_under_way_instead_of_pulling_the_process_out_from_under_it()
    {
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var calls = 0;
        var process = Wrap(StartPing(), p =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                Assert.True(release.Wait(Limit));
            }

            p.Kill(entireProcessTree: true);
        });
        var killing = Task.Run(process.Kill);
        Assert.True(entered.Wait(Limit));

        var disposing = Task.Run(async () => await process.DisposeAsync());
        await Task.Delay(500);
        Assert.False(disposing.IsCompleted);

        release.Set();

        await killing.WaitAsync(Limit);
        await disposing.WaitAsync(Limit);
    }

    [Fact]
    public async Task Kill_after_dispose_does_nothing()
    {
        var process = Wrap(StartPing(), p => p.Kill(entireProcessTree: true));
        await process.DisposeAsync();

        process.Kill();
    }
}
