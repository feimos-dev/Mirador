namespace Mirador.Core.Tests;

public sealed class BluetoothFactAttribute : FactAttribute
{
    public const string Variable = "MIRADOR_BLUETOOTH_TESTS";
    public const string StationsVariable = "MIRADOR_BLUETOOTH_STATIONS";

    public BluetoothFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"Bluetooth test: set {Variable}=1 and {StationsVariable}=<station names, comma-separated> on a PC near the base stations. It switches them.";
        }
    }
}
