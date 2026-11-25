using System.Diagnostics;

namespace Mirador.Core.Build;

public sealed record CommandSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null);

public interface ICommandRunner
{
    Task<int> RunAsync(CommandSpec spec, Action<string> onLine, CancellationToken ct);
}

public sealed class CommandRunner : ICommandRunner
{
    public async Task<int> RunAsync(CommandSpec spec, Action<string> onLine, CancellationToken ct)
    {
        var info = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (spec.WorkingDirectory is not null)
        {
            info.WorkingDirectory = spec.WorkingDirectory;
        }

        foreach (var (name, value) in spec.Environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                info.Environment.Remove(name);
            }
            else
            {
                info.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => Deliver(e.Data);
        process.ErrorDataReceived += (_, e) => Deliver(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new MiradorException($"{Path.GetFileName(spec.FileName)} couldn't be started: {e.Message.TrimEnd('.')}.");
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        return process.ExitCode;

        void Deliver(string? line)
        {
            if (line is not null)
            {
                onLine(line);
            }
        }
    }
}
