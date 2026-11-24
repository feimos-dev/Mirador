using System.Diagnostics;
using System.Text.Json.Nodes;
using Mirador.Core.Xr;

namespace Mirador.Core.OpenVr;

/// <summary>What Mirador asks of processes for OpenLamina's store: when one started, and ending one.</summary>
public interface IProcessProbe
{
    /// <summary>When process <paramref name="processId"/> was created, as Windows counts it (a FILETIME in UTC); null when it doesn't run or can't be asked.</summary>
    ulong? StartTime(int processId);

    /// <summary>
    /// Ends process <paramref name="processId"/> if it is still the one created at <paramref name="startTime"/>: waits
    /// <paramref name="grace"/> for it to end by itself, then asks its window to close and waits as long again, then
    /// ends it. False when it isn't that process or can't be ended.
    /// </summary>
    bool End(int processId, ulong startTime, TimeSpan grace);
}

/// <summary>The processes of this PC.</summary>
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

/// <summary>
/// Mirador's dashboard as OpenLamina's programs see it, through the store's dashboard document. While a live process is
/// named there as the dashboard's host, an OpenVR game leaves the system button and the dashboard to it and answers
/// OpenVR's dashboard calls from the document's <c>open</c>. The game that runs is in the scene document, which is how
/// the dashboard's exit button ends an OpenVR game: the game is asked to quit through the document (<c>quit_at</c>),
/// and ended if it stays.
/// </summary>
/// <param name="store">OpenLamina's store.</param>
/// <param name="processes">Asks and ends processes.</param>
/// <param name="log">Takes a line for Mirador's log.</param>
public sealed class OpenLaminaDashboard(OpenLaminaStore store, IProcessProbe processes, Action<string> log) : IDashboardRecord, IGameExit
{
    /// <summary>How long a game has to quit when asked, and then again once its window is asked to close, before it is ended.</summary>
    public static readonly TimeSpan QuitGrace = TimeSpan.FromSeconds(3);

    private readonly object gate = new();

    // Writes go one after another on a background thread: a held store lock never holds the dashboard up.
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
        // Only a host's document: without one, the document is the game's own dashboard's.
        if (data.ContainsKey("host_pid"))
        {
            data["open"] = open;
        }
    });

    /// <summary>Completes once every change asked for so far has been written or given up; for tests.</summary>
    public Task Flushed()
    {
        lock (gate)
        {
            return writing;
        }
    }

    public bool CanQuit() => RunningGame() is not null;

    /// <summary>Asks the game to quit and returns at once; it is ended in the background if it stays.</summary>
    public bool Quit()
    {
        if (RunningGame() is not { } game)
        {
            return false;
        }

        // An OpenVR game takes the time as a quit event of its own, as long as the request is fresh.
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

    /// <summary>The OpenVR game the scene document names, if that very process still runs.</summary>
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

    /// <summary>Runs <paramref name="work"/> after what was queued before it, on a background thread.</summary>
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
