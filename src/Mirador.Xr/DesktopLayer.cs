using System.Diagnostics;
using Mirador.Ipc;
using StereoKit;

namespace Mirador.Xr;

/// <summary>
/// The dashboard's end of the desktop program (<see cref="DesktopLayerApp"/>): started the first time a monitor is
/// shown and kept for the dashboard's life, so that showing one again is at once. Its log lines go to the dashboard's,
/// which Mirador writes into its own. Disposing it ends the program by closing its input.
/// </summary>
internal sealed class DesktopLayer : IDisposable
{
    /// <summary>How long the program gets to end its session once its input closes.</summary>
    private static readonly TimeSpan QuitLimit = TimeSpan.FromSeconds(3);

    private Process? process;
    private string? lastPlacement; // the last SHOW or HIDE sent
    private string? lastPoint;
    private bool unstarted;

    /// <summary>Whether the program ended by itself (it couldn't start its session, or it stopped): it isn't started again.</summary>
    public bool Failed => unstarted || process is { HasExited: true };

    /// <summary>Shows <paramref name="placement"/>, starting the program the first time; sends nothing that didn't change.</summary>
    public void Show(DesktopPlacement placement) => Send(DesktopLayerLink.FormatShow(placement), ref lastPlacement);

    public void Hide()
    {
        if (process is { HasExited: false })
        {
            Send(DesktopLayerLink.Hide, ref lastPlacement);
            lastPoint = null; // a hide drops the hand too
        }
    }

    /// <summary>Names the hand whose laser is on the desktop, if any: its trackpad scrolls.</summary>
    public void Point(Handed? hand)
    {
        if (process is { HasExited: false })
        {
            Send(DesktopLayerLink.FormatPoint(hand is null ? null : hand == Handed.Left), ref lastPoint);
        }
    }

    public void Dispose()
    {
        if (process is null)
        {
            return;
        }

        try
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(QuitLimit))
            {
                process.Kill();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
        }

        process.Dispose();
        process = null;
    }

    private void Send(string line, ref string? last)
    {
        if (line == last || Failed)
        {
            return;
        }

        try
        {
            if (process is null)
            {
                Start();
            }

            process!.StandardInput.WriteLine(line);
            process.StandardInput.Flush();
            last = line;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"The desktop program can't be reached: {e.Message}");
        }
    }

    private void Start()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!, "--desktop")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var started = new Process { StartInfo = info };
        started.OutputDataReceived += (_, e) => Forward(e.Data);
        started.ErrorDataReceived += (_, e) => Forward(e.Data);
        try
        {
            started.Start();
        }
        catch (Exception)
        {
            started.Dispose();
            unstarted = true;
            throw;
        }

        started.BeginOutputReadLine();
        started.BeginErrorReadLine();
        process = started;
    }

    private static void Forward(string? line)
    {
        if (!string.IsNullOrEmpty(line))
        {
            Console.WriteLine($"desktop: {line}");
        }
    }
}
