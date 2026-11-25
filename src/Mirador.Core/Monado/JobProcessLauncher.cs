using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Mirador.Core.Native;

namespace Mirador.Core.Monado;

public sealed class JobProcessLauncher : IProcessLauncher, IDisposable
{
    private readonly object gate = new();
    private readonly SafeFileHandle job;
    private readonly Func<int, bool>? exitKey;
    private bool disposed;

    public JobProcessLauncher(Func<int, bool>? exitKey = null)
    {
        this.exitKey = exitKey;
        try
        {
            job = Mirador.Ipc.ProcessJobs.CreateKillOnCloseJob();
        }
        catch (Win32Exception e)
        {
            throw new MiradorException(
                $"Couldn't set up the group that stops Monado when Mirador closes: {Reason(e)}. Restart Mirador; if this keeps happening, restart Windows.");
        }
    }

    public IRunningProcess Start(ProcessSpec spec)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            var process = StartProcess(spec);
            try
            {
                var error = Mirador.Ipc.ProcessJobs.AssignToJob(job, process.SafeHandle);
                if (error != 0 && !process.HasExited)
                {
                    throw new MiradorException(
                        $"Couldn't start {spec.FileName}: Windows wouldn't put it in the group that stops it when Mirador closes ({Reason(new Win32Exception(error))}).");
                }

                return new RunningProcess(process, job, exitKey: exitKey);
            }
            catch
            {
                Discard(process);
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            job.Dispose();
        }
    }

    private static Process StartProcess(ProcessSpec spec)
    {
        if (!Directory.Exists(spec.WorkingDirectory))
        {
            throw new MiradorException($"Couldn't start {spec.FileName}: the folder {spec.WorkingDirectory} it should run in doesn't exist.");
        }

        var info = new ProcessStartInfo
        {
            FileName = spec.FileName,
            Arguments = spec.Arguments,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.Environment.Clear();
        foreach (var (name, value) in spec.Environment)
        {
            info.Environment[name] = value;
        }

        try
        {
            return QuietProcess.Start(info)
                ?? throw new MiradorException($"Couldn't start {spec.FileName}: Windows didn't start it.");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            throw new MiradorException($"Couldn't start {spec.FileName}: {Reason(e, spec.FileName)}.");
        }
    }

    private static void Discard(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
        }

        process.Dispose();
    }

    private static string Reason(Exception e, string fileName = "") =>
        (e is Win32Exception win ? new Win32Exception(win.NativeErrorCode).Message : e.Message)
            .Replace("%1", fileName).TrimEnd().TrimEnd('.');
}
