namespace Mirador.Core;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;

    private SingleInstance(Mutex mutex) => this.mutex = mutex;

    public static SingleInstance? TryAcquire(string mutexName = @"Local\Mirador")
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName, out createdNew);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            throw new MiradorException(
                "Another program in this Windows session holds Mirador's name, so Mirador can't tell whether it already runs. Sign out and back in, or restart Windows.");
        }

        if (createdNew)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose() => mutex.Dispose();
}
