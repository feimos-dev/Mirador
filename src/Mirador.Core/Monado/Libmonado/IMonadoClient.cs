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

/// <summary>
/// One open connection to a running Monado through libmonado. Monado's device and tracking-origin lists are read when
/// the connection opens. Calls after <see cref="IDisposable.Dispose"/> throw <see cref="ObjectDisposedException"/>;
/// calls Monado doesn't answer throw <see cref="MiradorException"/>.
/// </summary>
public interface IMonadoClient : IDisposable
{
    /// <summary>The devices Monado created, in its device list's order.</summary>
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
}
