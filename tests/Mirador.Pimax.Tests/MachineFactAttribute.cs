namespace Mirador.Pimax.Tests;

public sealed class MachineFactAttribute : FactAttribute
{
    public const string Variable = "MIRADOR_MACHINE_TESTS";

    public MachineFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"Machine test: set {Variable}=1 on a PC with Pimax Play and the Dream Air on, Pimax's software stopped.";
        }
    }
}
