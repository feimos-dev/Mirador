using System.Text;
using Mirador.Core;
using Mirador.Core.Monado;
using Mirador.Core.Native;

namespace Mirador.DirectMode;

public sealed record DisplayCheckResult(bool Pass, string Message, string? WhatToDo, string Output)
{
    public bool Finished { get; init; } = true;

    public bool NotUsed { get; init; }
}

public static class DisplayCheck
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    public static async Task<DisplayCheckResult> RunAsync(
        string exe, Func<ProcessSpec, IConsoleProcess> start, CancellationToken ct, string arguments = "")
    {
        var lines = new List<string>();
        int exitCode;
        try
        {
            using var process = start(new ProcessSpec(
                exe, arguments, Path.GetDirectoryName(exe) ?? ".", ConsoleProcess.CurrentEnvironment()));
            process.Line += line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                }
            };
            process.CloseInput();
            try
            {
                exitCode = await process.Exited.WaitAsync(Limit, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await KillAsync(process).ConfigureAwait(false);
                return Result(false, $"The display check didn't finish within {Limit.TotalSeconds:0} s.", null, lines) with { Finished = false };
            }
            catch (OperationCanceledException)
            {
                await KillAsync(process).ConfigureAwait(false);
                throw;
            }
        }
        catch (MiradorException e)
        {
            return Result(false, e.Message, null, lines) with { Finished = false };
        }

        List<string> output;
        lock (lines)
        {
            output = [.. lines];
        }

        var verdict = output.LastOrDefault(l => After(l, "PASS:") is not null || After(l, "FAIL:") is not null || After(l, "NOT USED:") is not null);
        var notUsed = verdict is not null && After(verdict, "NOT USED:") is not null;
        var message = verdict is null ? null : After(verdict, "PASS:") ?? After(verdict, "FAIL:") ?? After(verdict, "NOT USED:");
        var whatToDo = output.Select(l => After(l, "what to do:")).LastOrDefault(t => t is not null);
        var pass = exitCode == 0;
        message ??= pass ? "Direct mode works." : $"The display check failed (exit code {exitCode}).";
        return Result(pass && !notUsed, message, whatToDo, output) with { NotUsed = notUsed };
    }

    private static async Task KillAsync(IConsoleProcess process)
    {
        process.Kill();
        await Task.WhenAny(process.Exited, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }

    private static DisplayCheckResult Result(bool pass, string message, string? whatToDo, List<string> lines)
    {
        var output = new StringBuilder();
        lock (lines)
        {
            foreach (var line in lines)
            {
                output.Append(line).Append('\n');
            }
        }

        return new DisplayCheckResult(pass, message, whatToDo, output.ToString());
    }

    private static string? After(string line, string marker)
    {
        for (var at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = line.IndexOf(marker, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (at == 0 || !char.IsLetterOrDigit(line[at - 1]))
            {
                var text = line[(at + marker.Length)..].Trim();
                return text.Length == 0 ? null : text;
            }
        }

        return null;
    }
}
