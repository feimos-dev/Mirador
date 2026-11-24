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
    /// Ends process <paramref name="processId"/> if it is still the one created at <paramref name="startTime"/>: asks
    /// its window to close, and ends it if it is still there after <paramref name="grace"/>. False when it isn't that
    /// process or can't be ended.
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

            if (process.CloseMainWindow() && process.WaitForExit(grace))
            {
                return true;
            }

            process.Kill(entireProcessTree: true);
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
/// the dashboard's exit button ends an OpenVR game.
/// </summary>
/// <param name="store">OpenLamina's store.</param>
/// <param name="processes">Asks and ends processes.</param>
/// <param name="log">Takes a line for Mirador's log.</param>
public sealed class OpenLaminaDashboard(OpenLaminaStore store, IProcessProbe processes, Action<string> log) : IDashboardRecord, IGameExit
{
    /// <summary>How long a game has to close by itself before it is ended.</summary>
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

    /// <summary>Completes once every change asked for so far has been written or given up; for tests and for quitting.</summary>
    public Task Flushed()
    {
        lock (gate)
        {
            return writing;
        }
    }

    public bool CanQuit() => RunningGame() is not null;

    public bool Quit() => RunningGame() is { } game && processes.End(game.ProcessId, game.StartTime, QuitGrace);

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

    private void Write(Action<JsonObject> edit)
    {
        lock (gate)
        {
            writing = writing.ContinueWith(
                _ =>
                {
                    try
                    {
                        if (store.Exists && !store.Update(OpenLaminaStore.Dashboard, edit))
                        {
                            log("OpenLamina's dashboard document couldn't be written; an OpenVR game may show its own dashboard.");
                        }
                    }
                    catch (Exception e)
                    {
                        log($"Writing OpenLamina's dashboard document failed: {e.Message}");
                    }
                },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }
}
