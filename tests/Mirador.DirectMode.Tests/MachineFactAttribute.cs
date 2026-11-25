namespace Mirador.DirectMode.Tests;

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
