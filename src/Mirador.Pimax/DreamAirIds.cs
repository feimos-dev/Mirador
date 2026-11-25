using Mirador.Core.Modules;

namespace Mirador.Pimax;

public static class DreamAirIds
{
    public static readonly UsbId Board = new(0x34A4, 0x0044);

    public static readonly UsbId EyeTracker = new(0x2104, 0x0561);

    public static readonly UsbId Receiver = new(0x28DE, 0x2300);
}
