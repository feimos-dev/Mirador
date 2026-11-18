using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Session;

namespace Mirador.Core.Playspace;

/// <summary>What a room setup step needs from the Monado session.</summary>
public interface IRoomSetupSession
{
    /// <summary>The running Monado's libmonado connection; null while Monado doesn't run (or has none).</summary>
    IMonadoClient? MonadoClient { get; }

    /// <summary>The running Monado's OpenXR runtime manifest, for the measuring program; null for the active runtime.</summary>
    string? RuntimeManifest { get; }

    /// <summary>The variables Mirador started Monado with.</summary>
    IReadOnlyDictionary<string, string>? MonadoEnvironment { get; }

    /// <summary>The Lighthouse universe in use (reads files).</summary>
    string? CurrentUniverse();

    /// <summary>Starts Monado again, as the Start button does; the start applies the kept room setups.</summary>
    Task<StartOutcome> StartMonadoAsync();
}

/// <summary>How bad a step's outcome is.</summary>
public enum RoomStepLevel
{
    Success,
    Warning,
    Error,
}

/// <summary>What a room setup step says when it is done.</summary>
public sealed record RoomStepOutcome(string Message, RoomStepLevel Level);

/// <summary>
/// One room setup step from start to end: measures with <see cref="RoomMeasuring"/>, keeps each tracked origin's new
/// offset in <paramref name="store"/> for the current Lighthouse universe, and sets it through the running Monado. With
/// "exit on disconnect", the measuring program's exit stops Monado whatever it measured: Monado is then started again,
/// after a failure too, and that start sets the kept offsets.
/// </summary>
public sealed class RoomSetupRun(RoomMeasuring measuring, RoomSetupStore store, IRoomSetupSession session, TimeProvider time)
{
    /// <summary>How long the step waits for Monado to stop after the measuring program closed, with "exit on disconnect".</summary>
    public TimeSpan StopWait { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Hears a failure the step didn't expect (it is shown in plain words too).</summary>
    public Action<string, Exception>? Log { get; init; }

    /// <summary>Runs <paramref name="step"/>; <paramref name="status"/> hears what it is doing. Never throws but for cancelling.</summary>
    /// <remarks>Talks to Monado and reads files: not on the UI thread.</remarks>
    public async Task<RoomStepOutcome> RunAsync(RoomStep step, Action<string> status, CancellationToken ct)
    {
        if (session.MonadoClient is not { } client)
        {
            return new RoomStepOutcome("Start Monado first.", RoomStepLevel.Error);
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
            var universe = session.CurrentUniverse();
            foreach (var setup in RoomMeasuring.Corrected(origins, step, poses, universe, time.GetLocalNow()))
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

            if (stops && !ReferenceEquals(session.MonadoClient, client))
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
            // Unexpected, but it goes the same way: shown, and Monado started again if the measuring program stopped it.
            Log?.Invoke("The room setup step failed", e);
            problem = $"Mirador couldn't set up the room: {e.Message.Trim().TrimEnd('.')}.";
        }

        // Once the measuring program has run, its exit stops Monado with "exit on disconnect", failed or not.
        if (programRan && stops)
        {
            await WaitForStopAsync(client, ct).ConfigureAwait(false);
            if (!ReferenceEquals(session.MonadoClient, client))
            {
                status("Starting Monado again.");
                var outcome = await session.StartMonadoAsync().ConfigureAwait(false);
                problem += outcome.Ok ? " Monado started again." : $" Monado didn't start again: {outcome.Message}";
            }
        }

        return new RoomStepOutcome(problem, RoomStepLevel.Error);
    }

    /// <summary>
    /// Waits until the session no longer has <paramref name="client"/> (Monado stopped), at most <see cref="StopWait"/>.
    /// A Start asked for while the session is still stopping waits for the stop.
    /// </summary>
    private async Task WaitForStopAsync(IMonadoClient client, CancellationToken ct)
    {
        var until = time.GetUtcNow() + StopWait;
        while (ReferenceEquals(session.MonadoClient, client) && time.GetUtcNow() < until)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), time, ct).ConfigureAwait(false);
        }
    }
}
