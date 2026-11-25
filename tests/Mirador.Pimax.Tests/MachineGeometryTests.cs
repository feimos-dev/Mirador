using Mirador.Core;
using Mirador.Core.Modules;

namespace Mirador.Pimax.Tests;

[Trait("Category", "Hardware")]
public class MachineGeometryTests
{
    [Trait("Category", "Machine")]
    [MachineFact]
    public async Task The_real_runtime_uploads_the_geometry()
    {
        using var dir = new TempDir();
        var pimax = PimaxInstall.Locate(new WindowsRegistryReader());
        Assert.NotNull(pimax);
        var uploader = new GeometryUploader(
            ConsoleProcess.Start, new WmiUsbDevices(), new SystemProcessList(), new AppPaths(dir.Path), TimeProvider.System);

        var result = await uploader.UploadAsync(pimax, EyeBlobVariants.Fallback, CancellationToken.None);

        Assert.True(result.Ok, $"{result.Message}\n{(File.Exists(result.LogPath) ? File.ReadAllText(result.LogPath) : "")}");
        Assert.False(string.IsNullOrWhiteSpace(result.TrackerSerial));
    }
}
