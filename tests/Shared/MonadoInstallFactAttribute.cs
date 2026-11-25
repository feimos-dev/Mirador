namespace Mirador.Tests;

public sealed class MonadoInstallFactAttribute : FactAttribute
{
    public const string Variable = "MIRADOR_TEST_MONADO_INSTALL";

    public MonadoInstallFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } folder
            || !File.Exists(Path.Combine(folder, "openxr_monado.json")))
        {
            Skip = $"Needs a Monado install: set {Variable} to one (a branch build's install folder, for example).";
        }
    }
}
