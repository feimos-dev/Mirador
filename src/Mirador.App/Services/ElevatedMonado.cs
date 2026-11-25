using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Mirador.Core.Elevation;
using Mirador.Ipc;

namespace Mirador.App.Services;

public static class ElevatedMonado
{
    public static bool IsAllowed() =>
        AllowElevatedCommand.CurrentUser() is { } user && new ElevatedConsent(new RegistryConsentValues()).IsAllowed(user);

    public static async Task<int> AllowAsync()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mirador.exe"))
        {
            Arguments = AllowElevatedCommand.Flag,
            Verb = "runas",
            UseShellExecute = true,
        };
        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return -1;
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ElevatedRuntime.PromptCancelled)
        {
            return ElevatedRuntime.PromptCancelled;
        }
        catch (Win32Exception e)
        {
            AppLog.Write("The elevated consent step couldn't be started", e);
            return -1;
        }
    }

    public static bool IsStandardAccount()
    {
        const int TokenElevationType = 18;
        const int Default = 1;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                return false;
            }

            return GetTokenInformation(identity.Token, TokenElevationType, out var type, sizeof(int), out _) && type == Default;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returned);
}
