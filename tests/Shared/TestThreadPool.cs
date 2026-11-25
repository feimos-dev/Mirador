using System.Runtime.CompilerServices;

namespace Mirador.Tests;

internal static class TestThreadPool
{
    public const int MinWorkerThreads = 64;

#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Raise()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, MinWorkerThreads), io);
    }
}
