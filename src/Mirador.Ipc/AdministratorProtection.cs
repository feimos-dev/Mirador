using Microsoft.Win32;

namespace Mirador.Ipc;

public static class AdministratorProtection
{
    public const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    public const int On = 2;

    public static bool IsOn()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(KeyPath);
            return IsOn(key?.GetValue("TypeOfAdminApprovalMode"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static bool IsOn(object? value) => value is int mode && mode == On;
}
