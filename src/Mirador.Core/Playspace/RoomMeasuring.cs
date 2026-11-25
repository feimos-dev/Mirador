using System.Globalization;
using Mirador.Core.Build;
using Mirador.Core.Lighthouse;
using Mirador.Core.Monado.Libmonado;
using Mirador.Ipc;

namespace Mirador.Core.Playspace;

public sealed record TrackedOrigin(uint Index, string Name, MonadoPose Offset);

public enum RoomStep
{
    Floor,

    Forward,
}

public sealed class RoomMeasuring(ICommandRunner runner, string xrCheck)
{
    public const int TimeoutSeconds = 30;

    public TimeSpan Limit { get; init; } = TimeSpan.FromSeconds(TimeoutSeconds + 15);

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

    private static readonly string[] MonadoFalse = ["false", "FALSE", "off", "OFF", "no", "NO", "n", "N", "f", "F", "0"];

    public static bool StopsMonado(IReadOnlyDictionary<string, string>? environment)
    {
        var value = environment?
            .LastOrDefault(e => string.Equals(e.Key, MonadoSettingsEnvironment.ExitOnDisconnect, StringComparison.OrdinalIgnoreCase))
            .Value;
        return value is not null && !MonadoFalse.Contains(value, StringComparer.Ordinal);
    }

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

    public const float FloorBelowHeadM = 0.6f;

    public static IReadOnlyList<RoomSetup> Corrected(
        IReadOnlyList<TrackedOrigin> origins, RoomStep step, XrPoses poses, string? universe, DateTimeOffset now, SteamVrBase? steamVrBase = null)
    {
        var floorY = 0f;
        var yaw = 0f;
        if (step == RoomStep.Floor)
        {
            floorY = RoomSetupMath.FloorY(poses.Left, poses.Right)
                ?? throw new MiradorException("No controller was tracked. Turn them on, lay them on the floor and measure again.");
            if (poses.Head.Y - (floorY + RoomSetupMath.ControllerRestHeightM) < FloorBelowHeadM)
            {
                throw new MiradorException("The controllers weren't well below the headset. Lay them all on the floor and measure again.");
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
