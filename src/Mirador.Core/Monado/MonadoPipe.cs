using Mirador.Core.Native;
using Mirador.Ipc;

namespace Mirador.Core.Monado;

public interface IMonadoPipe
{
    string NameFor(IReadOnlyDictionary<string, string> env);

    bool Exists(string name);
}

public sealed class MonadoPipe : IMonadoPipe
{
    private readonly Func<string, string> throughAnsi;

    public MonadoPipe() : this(Mirador.Ipc.AnsiCodePage.RoundTrip)
    {
    }

    internal MonadoPipe(Func<string, string> throughAnsi) => this.throughAnsi = throughAnsi;

    public string NameFor(IReadOnlyDictionary<string, string> env) => MonadoPipeName.NameFor(env, throughAnsi);

    public static string TempPathFrom(IReadOnlyDictionary<string, string> env) => MonadoPipeName.TempPathFrom(env);

    public bool Exists(string name) => Mirador.Ipc.ProcessJobs.PipeExists(name);
}
