using Mirador.Core.BaseStations;

namespace Mirador.Core.Tests.BaseStations;

internal sealed class FakeStationRadio : IStationRadio
{
    private readonly object _gate = new();
    private readonly List<Action<Advert>> _watches = [];
    private readonly List<string> _steps = [];
    private readonly List<Connection> _connections = [];
    private readonly List<Write> _writes = [];
    private readonly Dictionary<ulong, Func<Exception>> _unreachable = [];
    private readonly HashSet<ulong> _hanging = [];
    private readonly Dictionary<ulong, TaskCompletionSource> _held = [];
    private readonly List<(Func<bool> Done, TaskCompletionSource Signal)> _waits = [];
    private BluetoothState _state = BluetoothState.Ready;
    private int _watchCalls;
    private int _openLinks;
    private int _mostOpenLinks;

    public sealed record Connection(string Address, bool RandomAddress);

    public sealed record Write(string Address, Guid Service, Guid Characteristic, string Value);

    public BluetoothState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }

        set
        {
            lock (_gate)
            {
                _state = value;
            }

            StateChanged?.Invoke();
        }
    }

    public event Action? StateChanged;

    public bool NeverAnswers { get; set; }

    public Action<ulong>? OnConnect { get; set; }

    public Action<ulong>? OnWrite { get; set; }

    public Func<Exception>? WatchFailure { get; set; }

    public Func<Exception>? UnwatchFailure { get; set; }

    public bool Watching
    {
        get
        {
            lock (_gate)
            {
                return _watches.Count > 0;
            }
        }
    }

    public int WatchCalls
    {
        get
        {
            lock (_gate)
            {
                return _watchCalls;
            }
        }
    }

    public int MostOpenLinks
    {
        get
        {
            lock (_gate)
            {
                return _mostOpenLinks;
            }
        }
    }

    public int OpenLinks
    {
        get
        {
            lock (_gate)
            {
                return _openLinks;
            }
        }
    }

    public string[] Steps
    {
        get
        {
            lock (_gate)
            {
                return [.. _steps];
            }
        }
    }

    public Connection[] Connections
    {
        get
        {
            lock (_gate)
            {
                return [.. _connections];
            }
        }
    }

    public Write[] Writes
    {
        get
        {
            lock (_gate)
            {
                return [.. _writes];
            }
        }
    }

    public Connection[] ConnectionsTo(ulong address) =>
        [.. Connections.Where(c => c.Address == StationProtocol.FormatAddress(address))];

    public string[] WritesTo(ulong address) =>
        [.. Writes.Where(w => w.Address == StationProtocol.FormatAddress(address)).Select(w => w.Value)];

    public void Unreachable(ulong address, Func<Exception>? failure = null)
    {
        lock (_gate)
        {
            _unreachable[address] = failure ?? (() => new StationException("Windows couldn't connect to the base station over Bluetooth."));
        }
    }

    public void Reachable(ulong address)
    {
        lock (_gate)
        {
            _unreachable.Remove(address);
        }
    }

    public void Hang(ulong address)
    {
        lock (_gate)
        {
            _hanging.Add(address);
        }
    }

    public TaskCompletionSource Hold(ulong address)
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _held[address] = hold;
        }

        return hold;
    }

    public Task Until(Func<FakeStationRadio, bool> done)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waits.Add((() => done(this), signal));
        }

        Check();
        return signal.Task;
    }

    public void Advertise(Advert advert)
    {
        Action<Advert>[] watches;
        lock (_gate)
        {
            watches = [.. _watches];
        }

        if (watches.Length == 0)
        {
            throw new InvalidOperationException("Nothing is watching the radio, so the advert would be lost.");
        }

        foreach (var heard in watches)
        {
            heard(advert);
        }
    }

    public Task StartAsync() => NeverAnswers ? new TaskCompletionSource().Task : Task.CompletedTask;

    public IDisposable Watch(Action<Advert> heard)
    {
        if (WatchFailure is { } failure)
        {
            throw failure();
        }

        lock (_gate)
        {
            _watches.Add(heard);
            _watchCalls++;
        }

        return new Unwatch(this, heard);
    }

    public async Task<IStationLink> ConnectAsync(ulong address, bool randomAddress, CancellationToken ct)
    {
        var text = StationProtocol.FormatAddress(address);
        Func<Exception>? failure;
        TaskCompletionSource? hold;
        bool hangs;
        lock (_gate)
        {
            _connections.Add(new Connection(text, randomAddress));
            _steps.Add($"connect {text}");
            failure = _unreachable.GetValueOrDefault(address);
            hangs = NeverAnswers || _hanging.Contains(address);
            _held.Remove(address, out hold);
        }

        Check();
        OnConnect?.Invoke(address);
        if (hangs)
        {
            await new TaskCompletionSource().Task;
        }

        if (hold is not null)
        {
            await hold.Task;
        }

        if (failure is not null)
        {
            throw failure();
        }

        lock (_gate)
        {
            _openLinks++;
            _mostOpenLinks = Math.Max(_mostOpenLinks, _openLinks);
        }

        return new Link(this, address);
    }

    private void Check()
    {
        (Func<bool> Done, TaskCompletionSource Signal)[] waits;
        lock (_gate)
        {
            waits = [.. _waits];
        }

        foreach (var wait in waits)
        {
            if (!wait.Done())
            {
                continue;
            }

            lock (_gate)
            {
                _waits.Remove(wait);
            }

            wait.Signal.TrySetResult();
        }
    }

    private void Record(string step, Write? write = null)
    {
        lock (_gate)
        {
            _steps.Add(step);
            if (write is not null)
            {
                _writes.Add(write);
            }
        }

        Check();
    }

    private sealed class Unwatch(FakeStationRadio radio, Action<Advert> heard) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (radio._gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                radio._watches.Remove(heard);
            }

            if (radio.UnwatchFailure is { } failure)
            {
                throw failure();
            }
        }
    }

    private sealed class Link(FakeStationRadio radio, ulong address) : IStationLink
    {
        private bool _closed;

        public async Task WriteAsync(Guid service, Guid characteristic, byte[] value, CancellationToken ct)
        {
            if (radio.NeverAnswers)
            {
                await new TaskCompletionSource().Task;
            }

            ct.ThrowIfCancellationRequested();
            var text = StationProtocol.FormatAddress(address);
            var hex = Convert.ToHexString(value);
            radio.Record($"write {text} {hex}", new Write(text, service, characteristic, hex));
            radio.OnWrite?.Invoke(address);
        }

        public ValueTask DisposeAsync()
        {
            lock (radio._gate)
            {
                if (_closed)
                {
                    return ValueTask.CompletedTask;
                }

                _closed = true;
                radio._openLinks--;
            }

            radio.Record($"close {StationProtocol.FormatAddress(address)}");
            return ValueTask.CompletedTask;
        }
    }
}
