namespace Mirador.Core.Monado.Libmonado;

public sealed record MonadoDevice(int Index, string? Role, string Name, string? Serial, uint TrackingOrigin, bool HasBattery, bool Charging, float Charge);

public readonly record struct MonadoPose(float Qx, float Qy, float Qz, float Qw, float X, float Y, float Z)
{
    public static MonadoPose Identity { get; } = new(0, 0, 0, 1, 0, 0, 0);
}

public sealed record MonadoApp(uint Id, string Name, bool Shown, bool SessionRunning, bool Overlay, bool InputsBlocked);

public interface IMonadoClient : IDisposable
{
    IReadOnlyList<MonadoDevice> Devices();

    bool OffersOriginOffsets { get; }

    IReadOnlyList<string> TrackingOrigins();

    MonadoPose GetOriginOffset(uint origin);

    void SetOriginOffset(uint origin, MonadoPose offset);

    bool OffersApps => false;

    IReadOnlyList<MonadoApp> Apps() => [];

    void ShowApp(uint id) => throw new NotSupportedException("This libmonado doesn't pick the app shown.");

    bool OffersInputBlocks => false;

    void BlockInputs(uint id, bool blocked) => throw new NotSupportedException("This libmonado doesn't block an app's inputs.");

    bool OffersStageOffset => false;

    MonadoPose GetStageOffset() => throw new NotSupportedException("This libmonado doesn't move the stage.");

    void SetStageOffset(MonadoPose offset) => throw new NotSupportedException("This libmonado doesn't move the stage.");

    bool OffersLocalRecentre => false;

    void RecentreLocalSpaces() => throw new NotSupportedException("This libmonado doesn't recentre the local spaces.");
}
