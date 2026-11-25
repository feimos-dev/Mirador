using Mirador.Core.Modules;

namespace Mirador.Pimax;

public sealed record PimaxModel(UsbId Board, string Name, bool EyeSteps, bool Lighthouse, bool Tested);

public static class PimaxModels
{
    public static readonly PimaxModel CrystalLight = new(new UsbId(0x34A4, 0x0018), "Pimax Crystal Light", EyeSteps: false, Lighthouse: false, Tested: false);
    public static readonly PimaxModel CrystalSuper = new(new UsbId(0x34A4, 0x0040), "Pimax Crystal Super", EyeSteps: false, Lighthouse: false, Tested: false);

    public static readonly PimaxModel DreamAirSe = new(new UsbId(0x34A4, 0x0042), "Pimax Dream Air SE", EyeSteps: false, Lighthouse: true, Tested: false);

    public static readonly PimaxModel DreamAir = new(DreamAirIds.Board, "Pimax Dream Air", EyeSteps: true, Lighthouse: true, Tested: true);

    public static IReadOnlyList<PimaxModel> All { get; } = [CrystalLight, CrystalSuper, DreamAirSe, DreamAir];

    public static PimaxModel? Detect(IReadOnlySet<UsbId> devices) =>
        All.OrderByDescending(m => m.Tested).FirstOrDefault(m => devices.Contains(m.Board));

    public static PimaxModel DetectOrDreamAir(IReadOnlySet<UsbId> devices) => Detect(devices) ?? DreamAir;
}
