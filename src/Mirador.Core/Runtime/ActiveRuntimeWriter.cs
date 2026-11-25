using Microsoft.Win32;

namespace Mirador.Core.Runtime;

public static class ActiveRuntimeWriter
{
    public static void Write(string manifest)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.CreateSubKey(@"SOFTWARE\Khronos\OpenXR\1", writable: true);
        key.SetValue("ActiveRuntime", manifest, RegistryValueKind.String);
    }
}
