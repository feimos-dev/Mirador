using System.Diagnostics;
using System.Text.Json.Nodes;
using Mirador.Core.Xr;

namespace Mirador.Core.OpenVr;

public interface IProcessProbe
{
    ulong? StartTime(int processId);

    bool End(int processId, ulong startTime, TimeSpan grace);
}

public sealed class ProcessProbe : IProcessProbe
{
    public ulong? StartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : (ulong)process.StartTime.ToFileTimeUtc();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    public bool End(int processId, ulong startTime, TimeSpan grace)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || (ulong)process.StartTime.ToFileTimeUtc() != startTime)
            {
                return false;
            }

            if (process.WaitForExit(grace) || (process.CloseMainWindow() && process.WaitForExit(grace)))
            {
                return true;
            }

            process.Kill();
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }
}

public sealed class OpenLaminaDashboard(OpenLaminaStore store, IProcessProbe processes, Action<string> log) : IDashboardRecord, IGameExit
{
    public static readonly TimeSpan QuitGrace = TimeSpan.FromSeconds(3);

    private readonly object gate = new();

    private Task writing = Task.CompletedTask;

    public void Hosted(int? processId) => Write(data =>
    {
        if (processId is { } id && processes.StartTime(id) is { } start)
        {
            data["host_pid"] = id;
            data["host_start"] = start;
            data["open"] = false;
        }
        else
        {
            data.Remove("host_pid");
            data.Remove("host_start");
            data["open"] = false;
        }
    });

    public void Opened(bool open) => Write(data =>
    {
        if (data.ContainsKey("host_pid"))
        {
            data["open"] = open;
        }
    });

    public Task Flushed()
    {
        lock (gate)
        {
            return writing;
        }
    }

    public bool CanQuit() => RunningGame() is not null;

    public bool Quit()
    {
        if (RunningGame() is not { } game)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        Write(data =>
        {
            if (data.ContainsKey("host_pid"))
            {
                data["quit_at"] = now;
            }
        });
        Queue(() =>
        {
            if (!processes.End(game.ProcessId, game.StartTime, QuitGrace))
            {
                log("The game couldn't be ended from the dashboard.");
            }
        });
        return true;
    }

    private (int ProcessId, ulong StartTime)? RunningGame()
    {
        if (store.Read(OpenLaminaStore.Scene) is not { } scene
            || Number(scene["pid"]) is not { } pid || pid is 0 or > int.MaxValue
            || Number(scene["start"]) is not { } start || start == 0)
        {
            return null;
        }

        return processes.StartTime((int)pid) == start ? ((int)pid, start) : null;
    }

    private static ulong? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<ulong>(out var number) ? number : null;

    private void Write(Action<JsonObject> edit) => Queue(() =>
    {
        if (store.Exists && !store.Update(OpenLaminaStore.Dashboard, edit))
        {
            log("OpenLamina's dashboard document couldn't be written; an OpenVR game may show its own dashboard.");
        }
    });

    private void Queue(Action work)
    {
        lock (gate)
        {
            writing = writing.ContinueWith(
                _ =>
                {
                    try
                    {
                        work();
                    }
                    catch (Exception e)
                    {
                        log($"OpenLamina's store: {e.Message}");
                    }
                },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }
}
