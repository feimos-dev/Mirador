using Mirador.Core.Build;

namespace Mirador.Core.Tests.Build;

internal sealed class FakeCommandRunner : ICommandRunner
{
    public List<CommandSpec> Commands { get; } = [];

    public Func<CommandSpec, (string[] Lines, int Code)> Answer { get; set; } = _ => ([], 0);

    public Func<CommandSpec, CancellationToken, Task>? Effect { get; set; }

    public async Task<int> RunAsync(CommandSpec spec, Action<string> onLine, CancellationToken ct)
    {
        Commands.Add(spec);
        ct.ThrowIfCancellationRequested();
        if (Effect is not null)
        {
            await Effect(spec, ct);
        }

        var (lines, code) = Answer(spec);
        foreach (var line in lines)
        {
            onLine(line);
        }

        return code;
    }
}
