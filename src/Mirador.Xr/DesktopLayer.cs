using System.Diagnostics;
using Mirador.Ipc;
using StereoKit;

namespace Mirador.Xr;

internal sealed class DesktopLayer : IDisposable
{
    private static readonly TimeSpan QuitLimit = TimeSpan.FromSeconds(3);

    private Process? process;
    private string? lastPlacement;
    private string? lastPoint;
    private bool unstarted;

    public bool Failed => unstarted || process is { HasExited: true };

    public void Show(DesktopPlacement placement) => Send(DesktopLayerLink.FormatShow(placement), ref lastPlacement);

    public void Hide()
    {
        if (process is { HasExited: false })
        {
            Send(DesktopLayerLink.Hide, ref lastPlacement);
            lastPoint = null;
        }
    }

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
