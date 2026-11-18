namespace Mirador.Core.Tests;

/// <summary>
/// A test that reads this machine's real state without changing it. It is skipped, not failed, unless the
/// environment variable <c>MIRADOR_MACHINE_TESTS</c> is <c>1</c>.
/// </summary>
public sealed class MachineFactAttribute : FactAttribute
{
    public const string Variable = "MIRADOR_MACHINE_TESTS";

    public MachineFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"Machine test: set {Variable}=1 to run it on this PC.";
        }
    }
}
