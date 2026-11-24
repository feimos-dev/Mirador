namespace Mirador.Core.Monado.Libmonado;

/// <summary>A device Monado created, as libmonado describes it.</summary>
/// <param name="Index">The device's index in Monado's device list.</param>
/// <param name="Role">
/// The first role Monado gives the device, of <c>head</c>, <c>eyes</c>, <c>left</c>, <c>right</c>, <c>gamepad</c>,
/// <c>hand-tracking-unobstructed-left</c> and <c>hand-tracking-unobstructed-right</c>; null when it has none of them.
/// </param>
/// <param name="Name">The name the driver gives the device.</param>
/// <param name="Serial">The device's serial number; null when the driver gives none.</param>
/// <param name="TrackingOrigin">The index of the device's tracking origin in <see cref="IMonadoClient.TrackingOrigins"/>.</param>
/// <param name="HasBattery">Whether the device reports a battery.</param>
/// <param name="Charging">Whether the battery is charging.</param>
/// <param name="Charge">The battery's charge, from 0 to 1.</param>
public sealed record MonadoDevice(int Index, string? Role, string Name, string? Serial, uint TrackingOrigin, bool HasBattery, bool Charging, float Charge);

/// <summary>A pose as libmonado passes it: an orientation quaternion, then a position in metres.</summary>
public readonly record struct MonadoPose(float Qx, float Qy, float Qz, float Qw, float X, float Y, float Z)
{
    /// <summary>No rotation, no offset.</summary>
    public static MonadoPose Identity { get; } = new(0, 0, 0, 1, 0, 0, 0);
}

/// <summary>An app connected to Monado, as libmonado describes it.</summary>
/// <param name="Id">Monado's id for the app, for <see cref="IMonadoClient.ShowApp"/> and <see cref="IMonadoClient.BlockInputs"/>.</param>
/// <param name="Name">The application name the app gave OpenXR.</param>
/// <param name="Shown">Whether it is the app Monado shows, its primary app.</param>
/// <param name="SessionRunning">Whether the app has begun its session.</param>
/// <param name="Overlay">Whether its session is an overlay, drawn above the app shown.</param>
/// <param name="InputsBlocked">Whether Monado keeps its inputs from it.</param>
public sealed record MonadoApp(uint Id, string Name, bool Shown, bool SessionRunning, bool Overlay, bool InputsBlocked);

/// <summary>
/// One open connection to a running Monado through libmonado. Monado's device and tracking-origin lists are read when
/// the connection opens. Calls after <see cref="IDisposable.Dispose"/> throw <see cref="ObjectDisposedException"/>;
/// calls Monado doesn't answer throw <see cref="MiradorException"/>.
/// </summary>
public interface IMonadoClient : IDisposable
{
    /// <summary>The devices Monado created, in its device list's order.</summary>
    /// <exception cref="MiradorException">Monado's answer can't be read, or it didn't answer.</exception>
    IReadOnlyList<MonadoDevice> Devices();

    /// <summary>
    /// Whether this libmonado reads and moves tracking origins: <see cref="TrackingOrigins"/>,
    /// <see cref="GetOriginOffset"/> and <see cref="SetOriginOffset"/> throw <see cref="NotSupportedException"/> when not.
    /// </summary>
    bool OffersOriginOffsets { get; }

    /// <summary>The names of Monado's tracking origins; a position in this list is an origin's index.</summary>
    IReadOnlyList<string> TrackingOrigins();

    /// <summary>The offset of tracking origin <paramref name="origin"/>.</summary>
    MonadoPose GetOriginOffset(uint origin);

    /// <summary>Moves tracking origin <paramref name="origin"/> to <paramref name="offset"/>.</summary>
    void SetOriginOffset(uint origin, MonadoPose offset);

    /// <summary>
    /// Whether this libmonado lists Monado's apps and picks the one shown: <see cref="Apps"/> is empty and
    /// <see cref="ShowApp"/> throws <see cref="NotSupportedException"/> when not.
    /// </summary>
    bool OffersApps => false;

    /// <summary>The apps connected to Monado now, in its client list's order; this connection itself is among them.</summary>
    /// <exception cref="MiradorException">Monado didn't answer.</exception>
    IReadOnlyList<MonadoApp> Apps() => [];

    /// <summary>Makes app <paramref name="id"/> the one Monado shows.</summary>
    void ShowApp(uint id) => throw new NotSupportedException("This libmonado doesn't pick the app shown.");

    /// <summary>Whether this libmonado blocks an app's inputs: <see cref="BlockInputs"/> throws <see cref="NotSupportedException"/> when not.</summary>
    bool OffersInputBlocks => false;

    /// <summary>
    /// Keeps the inputs (buttons, triggers, sticks) from app <paramref name="id"/>, or gives them back. Its poses, hand
    /// tracking and haptics are never blocked here: any block on them is lifted.
    /// </summary>
    void BlockInputs(uint id, bool blocked) => throw new NotSupportedException("This libmonado doesn't block an app's inputs.");

    /// <summary>
    /// Whether this libmonado reads and moves the stage, the reference space every app's floor and forward come from:
    /// <see cref="GetStageOffset"/> and <see cref="SetStageOffset"/> throw <see cref="NotSupportedException"/> when not.
    /// </summary>
    bool OffersStageOffset => false;

    /// <summary>The stage's offset.</summary>
    MonadoPose GetStageOffset() => throw new NotSupportedException("This libmonado doesn't move the stage.");

    /// <summary>Moves the stage to <paramref name="offset"/>.</summary>
    void SetStageOffset(MonadoPose offset) => throw new NotSupportedException("This libmonado doesn't move the stage.");
}
