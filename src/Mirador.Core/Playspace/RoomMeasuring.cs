using System.Globalization;
using Mirador.Core.Build;
using Mirador.Core.Lighthouse;
using Mirador.Core.Monado.Libmonado;
using Mirador.Ipc;

namespace Mirador.Core.Playspace;

/// <summary>A tracking origin a room setup moves: its index and name in libmonado's list, and its offset now.</summary>
public sealed record TrackedOrigin(uint Index, string Name, MonadoPose Offset);

/// <summary>A step of the room setup.</summary>
public enum RoomStep
{
    /// <summary>A controller lies on the floor: the floor goes to y = 0.</summary>
    Floor,

    /// <summary>The head looks where forward should be: that direction goes to −Z.</summary>
    Forward,
}

/// <summary>
/// Measures the room with <c>xr-check --poses</c>, Mirador's small OpenXR client, run against the Monado that is
/// running, and works out the tracking origins' new offsets from what it measured.
/// </summary>
/// <param name="runner">Runs the client.</param>
/// <param name="xrCheck">The client: <c>{app}\tools\xr-check.exe</c>.</param>
public sealed class RoomMeasuring(ICommandRunner runner, string xrCheck)
{
    /// <summary>How long the client may take, all of it: it gives up by itself then.</summary>
    public const int TimeoutSeconds = 30;

    /// <summary>When Mirador ends a client that didn't give up by itself.</summary>
    public TimeSpan Limit { get; init; } = TimeSpan.FromSeconds(TimeoutSeconds + 15);

    /// <summary>
    /// Runs the client against the Monado of <paramref name="runtimeManifest"/> (null: the active OpenXR runtime) and
    /// returns what it measured.
    /// </summary>
    /// <exception cref="MiradorException">The client failed, gave no result, couldn't be started or didn't end in time.</exception>
    public async Task<XrPoses> MeasureAsync(string? runtimeManifest, CancellationToken ct)
    {
        var spec = new CommandSpec(
            xrCheck,
            ["--api", "d3d11", "--poses", "--timeout", TimeoutSeconds.ToString(CultureInfo.InvariantCulture)],
            null,
            runtimeManifest is null ? null : new Dictionary<string, string?> { ["XR_RUNTIME_JSON"] = runtimeManifest });
        var output = new List<string>();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Limit);
        int code;
        try
        {
            code = await runner.RunAsync(spec, line =>
            {
                lock (output)
                {
                    output.Add(line);
                }
            }, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MiradorException(
                $"The measuring program didn't finish within {Limit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s.");
        }

        lock (output)
        {
            if (code == 0 && XrPoses.Parse(output) is { } poses)
            {
                return poses;
            }

            throw new MiradorException(XrPoses.Failure(output) is { Length: > 0 } failure
                ? $"Mirador couldn't measure: {failure.TrimEnd('.')}."
                : $"The measuring program gave no result (exit code {code.ToString(CultureInfo.InvariantCulture)}).");
        }
    }

    /// <summary>The values Monado reads as off for a switch; any other value of a variable that is set is on.</summary>
    private static readonly string[] MonadoFalse = ["false", "FALSE", "off", "OFF", "no", "NO", "n", "N", "f", "F", "0"];

    /// <summary>
    /// Whether Monado runs with "exit on disconnect", so that the client's exit stops it: the variable is set and isn't one
    /// of the exact values Monado reads as off (its <c>debug_string_to_bool</c>). Names compare as Windows compares them.
    /// </summary>
    /// <param name="environment">The variables Mirador started Monado with.</param>
    public static bool StopsMonado(IReadOnlyDictionary<string, string>? environment)
    {
        var value = environment?
            .LastOrDefault(e => string.Equals(e.Key, MonadoSettingsEnvironment.ExitOnDisconnect, StringComparison.OrdinalIgnoreCase))
            .Value;
        return value is not null && !MonadoFalse.Contains(value, StringComparer.Ordinal);
    }

    /// <summary>The tracking origins of the headset and the controllers, in index order, with their offsets now.</summary>
    /// <exception cref="MiradorException">
    /// This libmonado can't move tracking origins, Monado lists no headset or controllers, or it didn't answer.
    /// </exception>
    public static IReadOnlyList<TrackedOrigin> TrackedOrigins(IMonadoClient client)
    {
        if (!client.OffersOriginOffsets)
        {
            throw new MiradorException(Libmonado.NotOffered);
        }

        var names = client.TrackingOrigins();
        var indices = client.Devices()
            .Where(d => d.Role is "head" or "left" or "right" && d.TrackingOrigin < names.Count)
            .Select(d => d.TrackingOrigin)
            .Distinct()
            .Order()
            .ToList();
        if (indices.Count == 0)
        {
            throw new MiradorException("Monado doesn't list a headset or controllers yet. Turn them on, then measure again.");
        }

        return [.. indices.Select(i => new TrackedOrigin(i, names[(int)i], client.GetOriginOffset(i)))];
    }

    /// <summary>How far below the head the lower controller must be for a floor step: less means it was still in hand.</summary>
    public const float FloorBelowHeadM = 0.6f;

    /// <summary>
    /// Each origin's room setup after <paramref name="step"/>: its offset corrected by what was measured
    /// (<see cref="RoomSetupMath.Correct"/>), for <paramref name="universe"/>, noting the SteamVR room setup it was
    /// measured on (<paramref name="steamVrBase"/>).
    /// </summary>
    /// <exception cref="MiradorException">
    /// The floor step, and neither controller was tracked or the lower one wasn't well below the head; the forward step,
    /// and the head looked up or down too far.
    /// </exception>
    public static IReadOnlyList<RoomSetup> Corrected(
        IReadOnlyList<TrackedOrigin> origins, RoomStep step, XrPoses poses, string? universe, DateTimeOffset now, SteamVrBase? steamVrBase = null)
    {
        var floorY = 0f;
        var yaw = 0f;
        if (step == RoomStep.Floor)
        {
            floorY = RoomSetupMath.FloorY(poses.Left, poses.Right)
                ?? throw new MiradorException("Neither controller was tracked. Turn one on, lay it on the floor and measure again.");
            if (poses.Head.Y - (floorY + RoomSetupMath.ControllerRestHeightM) < FloorBelowHeadM)
            {
                throw new MiradorException("The controller wasn't well below the headset. Lay it on the floor, then Measure floor.");
            }
        }
        else if (!RoomSetupMath.LooksAhead(poses.Head))
        {
            throw new MiradorException("Look straight ahead, then Set forward.");
        }
        else
        {
            yaw = RoomSetupMath.YawOf(poses.Head.Qx, poses.Head.Qy, poses.Head.Qz, poses.Head.Qw);
        }

        return [.. origins.Select(o => new RoomSetup(o.Name, universe, RoomSetupMath.Correct(o.Offset, floorY, yaw), now, steamVrBase))];
    }
}
