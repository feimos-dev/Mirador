namespace Mirador.Core.Monado;

public interface IRunningProcess : IAsyncDisposable
{
    int Id { get; }

    Task<int> Exited { get; }

    event Action<string>? Line;

    event Action<Exception>? ListenerFailed;

    void Kill();

    Task<bool> EndCleanlyAsync(TimeSpan limit) => Task.FromResult(false);
}

public interface IProcessLauncher
{
    IRunningProcess Start(ProcessSpec spec);
}
