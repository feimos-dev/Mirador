using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Session;

namespace Mirador.Core.Playspace;

public interface IRoomSetupSession
{
    IMonadoClient? MonadoClient { get; }

    string? RuntimeManifest { get; }

    IReadOnlyDictionary<string, string>? MonadoEnvironment { get; }

    SteamVrNow? SteamVr();

    SessionEnd? LastEnd { get; }

    bool Quitting { get; }

    Task<StartOutcome> StartMonadoAsync();
}

public enum RoomStepLevel
{
    Success,
    Warning,
    Error,
}

public sealed record RoomStepOutcome(string Message, RoomStepLevel Level);

public sealed class RoomSetupRun(RoomMeasuring measuring, RoomSetupStore store, IRoomSetupSession session, TimeProvider time)
{
    public TimeSpan StopWait { get; init; } = TimeSpan.FromSeconds(15);

    public Action<string, Exception>? Log { get; init; }

    public async Task<RoomStepOutcome> RunAsync(RoomStep step, Action<string> status, CancellationToken ct)
    {
        if (session.MonadoClient is not { } client)
        {
            return new RoomStepOutcome("Monado must be running.", RoomStepLevel.Error);
        }

        var stops = RoomMeasuring.StopsMonado(session.MonadoEnvironment);
        var measured = step == RoomStep.Floor ? "The floor is measured and kept" : "Forward is measured and kept";
        var set = step == RoomStep.Floor ? "The floor is set." : "Forward is set.";
        var programRan = false;
        string problem;
        try
        {
            var origins = RoomMeasuring.TrackedOrigins(client);
            status(step == RoomStep.Floor ? "Measuring the floor. Keep the controller still." : "Measuring forward. Keep looking ahead.");
            programRan = true;
            var poses = await measuring.MeasureAsync(session.RuntimeManifest, ct).ConfigureAwait(false);
            var steamVr = session.SteamVr();
            var universe = steamVr?.Pick.Id;
            foreach (var setup in RoomMeasuring.Corrected(origins, step, poses, universe, time.GetLocalNow(), steamVr?.Base))
            {
                store.Set(setup);
            }

            if (stops)
            {
                await WaitForStopAsync(client, ct).ConfigureAwait(false);
            }

            if (ReferenceEquals(session.MonadoClient, client) && RoomSetupApplier.Apply(client, store, universe) > 0)
            {
                return new RoomStepOutcome(set, RoomStepLevel.Success);
            }

            if (stops && StoppedByTheMeasurement(client))
            {
                status("Starting Monado again.");
                var outcome = await session.StartMonadoAsync().ConfigureAwait(false);
                return outcome.Ok
                    ? new RoomStepOutcome(set, RoomStepLevel.Success)
                    : new RoomStepOutcome($"{measured}, but Monado didn't start again: {outcome.Message}", RoomStepLevel.Warning);
            }

            return new RoomStepOutcome($"{measured}; Monado stopped, so it is set when Monado next starts.", RoomStepLevel.Warning);
        }
        catch (MiradorException e)
        {
            problem = e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = $"Mirador couldn't keep the room setup ({e.Message.Trim().TrimEnd('.')}).";
        }
        catch (Exception e) when (e is NotSupportedException or ObjectDisposedException)
        {
            problem = "Monado stopped while measuring. Start it and measure again.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log?.Invoke("The room setup step failed", e);
            problem = $"Mirador couldn't set up the room: {e.Message.Trim().TrimEnd('.')}.";
        }

        if (programRan && stops)
        {
            await WaitForStopAsync(client, ct).ConfigureAwait(false);
            if (StoppedByTheMeasurement(client))
            {
                status("Starting Monado again.");
                var outcome = await session.StartMonadoAsync().ConfigureAwait(false);
                problem += outcome.Ok ? " Monado started again." : $" Monado didn't start again: {outcome.Message}";
            }
        }

        return new RoomStepOutcome(problem, RoomStepLevel.Error);
    }

    private bool StoppedByTheMeasurement(IMonadoClient client) =>
        !ReferenceEquals(session.MonadoClient, client)
        && !session.Quitting
        && session.LastEnd is { Problem: false } end
        && end.Message != SessionController.StoppedMessage;

    private async Task WaitForStopAsync(IMonadoClient client, CancellationToken ct)
    {
        var until = time.GetUtcNow() + StopWait;
        while ((ReferenceEquals(session.MonadoClient, client) || session.LastEnd is null) && time.GetUtcNow() < until)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), time, ct).ConfigureAwait(false);
        }
    }
}
