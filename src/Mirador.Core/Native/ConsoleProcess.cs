using System.ComponentModel;
using System.Diagnostics;
using Mirador.Core;
using Mirador.Core.Monado;

namespace Mirador.Core.Native;

public interface IConsoleProcess : IDisposable
{
    event Action<string>? Line;

    bool HasExited { get; }

    Task<int> Exited { get; }

    void WriteLine(string s);

    void CloseInput();

    bool WaitForExit(TimeSpan t);

    void Kill();
}

public sealed class ConsoleProcess : IConsoleProcess
{
    private readonly Process process;
    private readonly object gate = new();
    private Action<string>? listeners;
    private List<string>? early = [];

    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ConsoleProcess(Process process)
    {
        this.process = process;
        process.OutputDataReceived += (_, e) => OnData(e.Data);
        process.ErrorDataReceived += (_, e) => OnData(e.Data);
        process.Exited += (_, _) => ended.TrySetResult();
        process.EnableRaisingEvents = true;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (process.HasExited)
        {
            ended.TrySetResult();
        }

        Exited = WaitAsync();
    }

    public event Action<string>? Line
    {
        add
        {
            if (value is null)
            {
                return;
            }

            lock (gate)
            {
                listeners += value;
                if (early is { } replay)
                {
                    early = null;
                    foreach (var line in replay)
                    {
                        value(line);
                    }
                }
            }
        }

        remove
        {
            lock (gate)
            {
                listeners -= value;
            }
        }
    }

    public bool HasExited => process.HasExited;

    public Task<int> Exited { get; }

    public static IConsoleProcess Start(ProcessSpec spec)
    {
        var info = new ProcessStartInfo(spec.FileName, spec.Arguments)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment.Clear();
        foreach (var (name, value) in spec.Environment)
        {
            info.Environment[name] = value;
        }

        try
        {
            var process = QuietProcess.Start(info)
                ?? throw new MiradorException($"Couldn't start {spec.FileName}.");
            return new ConsoleProcess(process);
        }
        catch (Win32Exception e)
        {
            throw new MiradorException($"Couldn't start {spec.FileName}: {e.Message.Trim().TrimEnd('.')}.");
        }
    }

    public static IReadOnlyDictionary<string, string> CurrentEnvironment()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                found[name] = value;
            }
        }

        return found;
    }

    public void WriteLine(string s)
    {
        try
        {
            process.StandardInput.WriteLine(s);
            process.StandardInput.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public void CloseInput()
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
        }
    }

    public bool WaitForExit(TimeSpan t) => process.WaitForExit(t);

    public void Kill()
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
        }
    }

    public void Dispose() => process.Dispose();

    private async Task<int> WaitAsync()
    {
        await ended.Task.ConfigureAwait(false);
        try
        {
            await process.WaitForExitAsync().WaitAsync(DrainGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private void OnData(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (gate)
        {
            if (early is not null)
            {
                early.Add(line);
                return;
            }

            listeners?.Invoke(line);
        }
    }
}
