using System.Runtime.InteropServices;
using Mirador.Core.Session;

namespace Mirador.App.Services;

internal sealed class EndSession
{
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const string Reason = "Switching off the base stations";

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly AppHost host;

    private readonly SubclassProc proc;

    private bool offNeeded;
    private bool blocking;

    public EndSession(IntPtr window, AppHost host)
    {
        this.host = host;
        proc = OnMessage;

        if (!SetWindowSubclass(window, proc, 3, 0))
        {
            AppLog.Write("Mirador can't tell when Windows shuts down or signs out, so it doesn't switch the base stations off then");
        }
    }

    private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == WmQueryEndSession)
        {
            offNeeded = OffNeeded();
            if (offNeeded && !blocking)
            {
                blocking = ShutdownBlockReasonCreate(window, Reason);
                if (!blocking)
                {
                    AppLog.Write($"Windows is ending the session: Mirador couldn't give Windows its reason (error {Marshal.GetLastPInvokeError()})");
                }
            }

            return 1;
        }

        if (message == WmEndSession)
        {
            if (wParam != 0 && (offNeeded || OffNeeded()))
            {
                Finish();
            }
            else if (wParam != 0 && host.Session.State != SessionState.Stopped)
            {
                AppLog.Write("Windows is ending the session: Mirador stops Monado");
                Wait(Task.Run(StopAsync));
            }

            offNeeded = false;
            if (blocking)
            {
                ShutdownBlockReasonDestroy(window);
                blocking = false;
            }
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private bool OffNeeded() =>
        host.Stations.OffPending
        || host.Stations.OffAtQuitWanted
        || (host.Session.CurrentProfile is { } running && host.Stations.OffFollows(running.Profile, running.Module));

    private void Finish()
    {
        AppLog.Write("Windows is ending the session: Mirador switches off the base stations and stops Monado");
        try
        {
            if (host.Session.CurrentProfile is { } running)
            {
                host.Stations.EndingNow(running.Profile, running.Module);
            }

            host.Stations.QuittingNow();
            var off = Task.Run(() => host.Stations.FlushAsync(Limit));
            var stop = Task.Run(StopAsync);
            Wait(Task.WhenAll(off, stop));
        }
        catch (Exception e)
        {
            AppLog.Write("Windows is ending the session: finishing failed", e);
        }
    }

    private static void Wait(Task work)
    {
        try
        {
            if (!work.Wait(Limit))
            {
                AppLog.Write($"Windows is ending the session: Mirador stopped waiting after {Limit.TotalSeconds:0} s");
            }
        }
        catch (AggregateException e)
        {
            AppLog.Write("Windows is ending the session: finishing failed", e);
        }
    }

    private async Task StopAsync()
    {
        try
        {
            await host.Session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            AppLog.Write("Stopping Monado as Windows ends the session failed", e);
        }
    }

    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);

    [DllImport("user32.dll")]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr window);
}
