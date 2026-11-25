using System.Diagnostics;

namespace Mirador.Core.Native;

public static class QuietProcess
{
    public static Process? Start(ProcessStartInfo info) => Win32.WithoutErrorBoxes(() => Process.Start(info));
}
