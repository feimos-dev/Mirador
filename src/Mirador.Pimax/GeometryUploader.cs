using System.Text.RegularExpressions;
using Mirador.Core;
using Mirador.Core.Native;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Ipc;

namespace Mirador.Pimax;

public sealed record GeometryResult(bool Ok, string? TrackerSerial, string Message, string LogPath);

public sealed partial class GeometryUploader(
    Func<ProcessSpec, IConsoleProcess> start, IUsbDevices usb, IProcessList processes, AppPaths paths, TimeProvider time)
{
    public const string Uploaded = "Geometry uploaded";

    internal const string NoTracker = "The eye tracker isn't on USB. Is the headset connected and switched on?";

    private const string NotUploaded = "The eye geometry wasn't uploaded, so gaze will be about 10° off";

    internal static readonly TimeSpan LicenceWait = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan SendWait = TimeSpan.FromSeconds(2);

    internal static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private static readonly string[] Holders = ["pi_server", "vrss_gaze_provider", PimaxNames.EyeRuntimeProcess];

    public async Task<GeometryResult> UploadAsync(PimaxInstall pimax, string variant, CancellationToken ct)
    {
        var logPath = Path.Combine(paths.GeometryDir, "runtime.log");
        if (!EyeBlobVariants.All.Contains(variant))
        {
            return new GeometryResult(false, null, $"{NotUploaded}: Mirador doesn't know the eye geometry variant {variant}.", logPath);
        }

        try
        {
            if (!usb.Present().Contains(DreamAirIds.EyeTracker))
            {
                return new GeometryResult(false, null, NoTracker, logPath);
            }
        }
        catch (MiradorException e)
        {
            return new GeometryResult(false, null, $"{NotUploaded}: {e.Message}", logPath);
        }

        if (processes.Snapshot().FirstOrDefault(p => Holders.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) is { } holder)
        {
            return new GeometryResult(
                false, null,
                $"{NotUploaded}: {holder.Name} is running and holds the eye tracker.",
                logPath);
        }

        var lines = new List<string>();
        var notes = new List<string>();
        try
        {
            await RunAsync(pimax, variant, lines, notes, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is MiradorException or IOException or UnauthorizedAccessException)
        {
            notes.Add(e.Message);
        }
        finally
        {
            WriteLog(logPath, lines, notes);
        }

        List<string> output;
        lock (lines)
        {
            output = [.. lines];
        }

        var (connected, created, serial) = Evaluate(output);
        return connected && created
            ? new GeometryResult(true, serial, Uploaded, logPath)
            : new GeometryResult(false, serial, $"{NotUploaded}. Details: {logPath}", logPath);
    }

    public static (bool Connected, bool Created, string? Serial) Evaluate(IReadOnlyList<string> lines)
    {
        string? serial = null;
        var created = false;
        foreach (var line in lines)
        {
            if (serial is null && OpenedDevice().Match(line) is { Success: true } match)
            {
                serial = match.Groups[1].Value;
            }

            created |= line.Contains("tobii_xr_create successful", StringComparison.OrdinalIgnoreCase);
        }

        return (serial is not null, created, serial);
    }

    private async Task RunAsync(PimaxInstall pimax, string variant, List<string> lines, List<string> notes, CancellationToken ct)
    {
        var runtimeDir = Path.Combine(paths.GeometryDir, "runtime");
        var outputDir = Path.Combine(paths.GeometryDir, "log");
        Directory.CreateDirectory(runtimeDir);
        Directory.CreateDirectory(outputDir);

        var blob = EyeBlobVariants.FileName(variant);
        var exe = Path.Combine(runtimeDir, PimaxNames.EyeRuntimeProcess + ".exe");
        File.Copy(pimax.TobiiRuntimeExe, exe, overwrite: true);
        File.Copy(pimax.BlobPath(variant), Path.Combine(runtimeDir, blob), overwrite: true);

        var outputArg = Path.Combine("..", "log");
        using var process = start(new ProcessSpec(exe, $"-x {blob} \"{outputArg}\"", runtimeDir, ConsoleProcess.CurrentEnvironment()));
        var licence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Line += line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }

            if (line.Contains("validating licenses", StringComparison.OrdinalIgnoreCase))
            {
                licence.TrySetResult();
            }
        };

        try
        {
            if (await WaitAsync(licence.Task, process.Exited, LicenceWait, ct).ConfigureAwait(false))
            {
                await WaitAsync(process.Exited, null, SendWait, ct).ConfigureAwait(false);
            }
            else if (!process.Exited.IsCompleted)
            {
                notes.Add($"No licence check within {LicenceWait.TotalSeconds:0} s.");
            }

            process.WriteLine("");
            process.CloseInput();
            if (!await WaitAsync(process.Exited, null, ExitWait, ct).ConfigureAwait(false))
            {
                notes.Add($"Didn't stop within {ExitWait.TotalSeconds:0} s of ENTER; ended it.");
                await KillAsync(process).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await KillAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    private async Task KillAsync(IConsoleProcess process)
    {
        process.Kill();
        using var stop = new CancellationTokenSource();
        await Task.WhenAny(process.Exited, Task.Delay(KillWait, time, stop.Token)).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
    }

    private async Task<bool> WaitAsync(Task task, Task? orElse, TimeSpan limit, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(limit, time, stop.Token);
        var first = await (orElse is null ? Task.WhenAny(task, delay) : Task.WhenAny(task, orElse, delay)).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return first == task;
    }

    private static void WriteLog(string logPath, List<string> lines, List<string> notes)
    {
        try
        {
            List<string> all;
            lock (lines)
            {
                all = [.. lines, .. notes.Select(note => "Mirador: " + note)];
            }

            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllLines(logPath, all);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"Opened device '([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex OpenedDevice();
}
