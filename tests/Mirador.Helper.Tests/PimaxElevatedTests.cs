using Mirador.Helper.ElevatedMonado;
using Mirador.Ipc;

namespace Mirador.Helper.Tests;

public class PimaxElevatedTests
{
    private static readonly UserFolders Folders = new(@"C:\Users\finn", @"C:\Users\finn\AppData\Roaming", @"C:\Users\finn\AppData\Local", @"C:\Users\finn\AppData\Local\Temp");

    [Fact]
    public void The_pimax_module_gets_the_dream_airs_table()
    {
        var pimax = ElevatedEnvironment.Build(
            new ElevatedStart(3000, 100, 6, false, PimaxEnvironment.ModuleId), Folders, @"C:\Program Files\Mirador\monado\server", @"C:\SteamVR");

        foreach (var (name, value) in DreamAirEnvironment.FixedEnvironment)
        {
            Assert.Equal(value, pimax[name]);
        }

        // The module adds its table and nothing else: no variable of a driver that isn't in upstream Monado.
        var none = ElevatedEnvironment.Build(
            new ElevatedStart(3000, 100, 6, false, ""), Folders, @"C:\Program Files\Mirador\monado\server", @"C:\SteamVR");
        Assert.Equal(
            DreamAirEnvironment.FixedEnvironment.Select(p => p.Key).Order(),
            pimax.Keys.Except(none.Keys).Order());

        Assert.Throws<ArgumentException>(() => ElevatedEnvironment.Build(
            new ElevatedStart(3000, 100, 6, false, "dreamair"), Folders, @"C:\Program Files\Mirador\monado\server", @"C:\SteamVR"));
    }
}
