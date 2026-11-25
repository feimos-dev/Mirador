namespace Mirador.Core.BaseStations;

public enum BluetoothState
{
    Ready,

    Off,

    Missing,
}

public sealed record Advert(ulong Address, bool RandomAddress, string Name, byte[]? ValveData);

public interface IStationLink : IAsyncDisposable
{
    Task WriteAsync(Guid service, Guid characteristic, byte[] value, CancellationToken ct);
}

public interface IStationRadio
{
    BluetoothState State { get; }

    event Action? StateChanged;

    Task StartAsync();

    IDisposable Watch(Action<Advert> heard);

    Task<IStationLink> ConnectAsync(ulong address, bool randomAddress, CancellationToken ct);
}

public sealed class StationException(string message, Exception? inner = null) : Exception(message, inner);
