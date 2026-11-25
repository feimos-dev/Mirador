using Mirador.Core;
using Mirador.Core.Modules;
using Mirador.Ipc;

namespace Mirador.Pimax;

public sealed class DreamAirChecks(
    IRegistryReader reg, IUsbDevices usb, Func<string, bool> fileExists, Func<string?> steamVrDir)
{
    private const string InstallPimax = "Install Pimax Play: Mirador uses its eye tracking runtime.";
    private const string RepairPimax = "Repair or update Pimax Play.";

    private static readonly (UsbId Id, string Name)[] Parts =
    [
        (DreamAirIds.Board, "headset board"),
        (DreamAirIds.EyeTracker, "eye tracker"),
        (DreamAirIds.Receiver, "Lighthouse receiver"),
    ];

    public IReadOnlyList<CheckResult> Run(string blobVariant) => Run(blobVariant, PimaxModels.DreamAir);

    public IReadOnlyList<CheckResult> Run(string blobVariant, PimaxModel model)
    {
        var pimax = PimaxInstall.Locate(reg);
        List<CheckResult> results = [PimaxPlay(pimax, model.EyeSteps)];
        if (model.EyeSteps)
        {
            results.Add(EyeGeometry(pimax, blobVariant));
        }

        if (model.Lighthouse)
        {
            results.Add(SteamVrCheck());
        }

        results.Add(Headset(model));
        return results;
    }

    public static string? FindSteamVr() =>
        SteamVr.FindInstall(
            SteamVr.SteamPathFromRegistry(), Environment.GetEnvironmentVariable("STEAMVR_PATH"), ReadTextOrNull, Directory.Exists);

    internal static string? ReadTextOrNull(string path) => ReadTextOrNull(path, Thread.Sleep);

    internal static string? ReadTextOrNull(string path, Action<TimeSpan> sleep)
    {
        const int Tries = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
            {
                return null;
            }
            catch (IOException) when (attempt < Tries)
            {
                sleep(TimeSpan.FromMilliseconds(100));
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    private CheckResult PimaxPlay(PimaxInstall? pimax, bool eyeTracking)
    {
        const string Label = "Pimax Play";
        if (pimax is null)
        {
            return eyeTracking
                ? new(Label, StatusLevel.Error, "Pimax Play isn't installed.", InstallPimax)
                : new(Label, StatusLevel.Ok, "Not installed (not needed for this headset)");
        }

        return !eyeTracking || fileExists(pimax.TobiiRuntimeExe)
            ? new(Label, StatusLevel.Ok, pimax.Version ?? "Installed")
            : new(Label, StatusLevel.Error, "Pimax Play's eye tracking runtime is missing.", RepairPimax);
    }

    private CheckResult EyeGeometry(PimaxInstall? pimax, string variant)
    {
        const string Label = "Eye geometry";
        if (!EyeBlobVariants.All.Contains(variant))
        {
            return new(Label, StatusLevel.Error, $"Mirador doesn't know the eye geometry variant {variant}.", "Pick your headset's variant in Profiles.");
        }

        var file = EyeBlobVariants.FileName(variant);
        if (pimax is null)
        {
            return new(Label, StatusLevel.Error, $"{file} comes with Pimax Play, which isn't installed.", InstallPimax);
        }

        return fileExists(pimax.BlobPath(variant))
            ? new(Label, StatusLevel.Ok, $"config {variant}")
            : new(Label, StatusLevel.Error, $"{file} is missing from Pimax Play.", RepairPimax);
    }

    private CheckResult SteamVrCheck()
    {
        const string Label = "SteamVR";
        if (steamVrDir() is not { } dir)
        {
            return new(
                Label, StatusLevel.Error, "SteamVR isn't installed.",
                "Install SteamVR from Steam. It doesn't need to run: Mirador uses its Lighthouse driver.");
        }

        return fileExists(SteamVr.DriverLighthouse(dir))
            ? new(Label, StatusLevel.Ok, "Lighthouse driver found")
            : new(
                Label, StatusLevel.Error, "SteamVR's Lighthouse driver is missing.",
                "Repair SteamVR: in Steam, open its Properties, then Installed Files, and verify its files.");
    }

    private CheckResult Headset(PimaxModel model)
    {
        const string Label = "Headset";
        IReadOnlySet<UsbId> present;
        try
        {
            present = usb.Present();
        }
        catch (MiradorException e)
        {
            return new(Label, StatusLevel.Warning, e.Message);
        }

        (UsbId Id, string Name)[] parts =
        [
            (model.Board, "headset board"),
            .. model.EyeSteps ? [Parts[1]] : Array.Empty<(UsbId, string)>(),
            .. model.Lighthouse ? [Parts[2]] : Array.Empty<(UsbId, string)>(),
        ];
        var missing = parts.Where(part => !present.Contains(part.Id)).Select(part => part.Name).ToList();
        return missing.Count == 0
            ? new(Label, StatusLevel.Ok, "Connected")
            : new(Label, StatusLevel.Warning, $"Not connected: {string.Join(", ", missing)}.", "Plug the headset in and switch it on.");
    }
}
