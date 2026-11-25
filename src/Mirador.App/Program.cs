using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.Elevation;
using Mirador.Core.OpenVr;
using Mirador.Core.Runtime;
using Mirador.Core.SignIn;
using Mirador.Ipc;

namespace Mirador.App;

public static partial class Program
{
    private static readonly TimeSpan ShowWait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(90);

    [STAThread]
    private static int Main(string[] args)
    {
        if (ConsoleExitKey.Requested(args) is { } monado)
        {
            return ConsoleExitKey.Send(monado) is null ? 0 : 1;
        }

        var parsed = AppArgs.Parse(args);

        if (parsed.AllowElevatedMonado)
        {
            AppLog.Open(AppPaths.Default);
            var allowed = AllowElevatedCommand.Run(
                new ElevatedConsent(new RegistryConsentValues()),
                AllowElevatedCommand.CurrentUser(),
                AllowElevatedCommand.UnsignedLighthouseFiles,
                AppLog.Write);
            if (allowed != 0 || (parsed.ApproveBuild is null && parsed.ApproveVariables is null))
            {
                return allowed;
            }
        }

        if (parsed.ApproveVariables is { } approveVariables)
        {
            AppLog.Open(AppPaths.Default);
            try
            {
                var owner = AllowElevatedCommand.CurrentUser()?.Value ?? throw new InvalidOperationException("Windows didn't say who you are.");
                Mirador.Ipc.ApprovedBuilds.ApproveVariables(
                    owner, approveVariables.ProfileId, AppContext.BaseDirectory, new Mirador.Ipc.RegistryApprovedBuildStore(), approveVariables.Variables);
                return 0;
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                AppLog.Write("Approving the variables failed", e);
                return 1;
            }
        }

        if (parsed.ApproveBuild is { } approve)
        {
            AppLog.Open(AppPaths.Default);
            try
            {
                var owner = AllowElevatedCommand.CurrentUser()?.Value ?? throw new InvalidOperationException("Windows didn't say who you are.");
                var (commit, source) = AppArgs.ApprovalTarget(approve.Commit, approve.Source);
                var bundled = commit == Mirador.Ipc.ApprovedBuilds.BundledCommit;
                var profileRoot = Mirador.Ipc.ApprovedBuilds.ProfileRoot(AppContext.BaseDirectory, owner, approve.ProfileId) + Path.DirectorySeparatorChar;
                var active = new RegistryRuntimeReader().ActiveRuntime();
                var runtimeWasThisProfiles = active is not null && Path.GetFullPath(active).StartsWith(profileRoot, StringComparison.OrdinalIgnoreCase);
                var build = Mirador.Ipc.ApprovedBuilds.Approve(
                    owner, approve.ProfileId, commit, source,
                    AppContext.BaseDirectory, new Mirador.Ipc.RegistryApprovedBuildStore(), approve.Variables, AppLog.Write);

                if (runtimeWasThisProfiles)
                {
                    if (!bundled && Path.IsPathRooted(build.Folder))
                    {
                        ActiveRuntimeWriter.Write(build.Manifest);
                    }
                    else if (Mirador.Ipc.MiradorMonado.Read(Path.Combine(AppContext.BaseDirectory, "monado")).Current is { } version
                        && Mirador.Ipc.MiradorMonado.ManifestOf(Path.Combine(AppContext.BaseDirectory, "monado"), version) is var miradors
                        && File.Exists(miradors))
                    {
                        ActiveRuntimeWriter.Write(miradors);
                    }
                }

                return 0;
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                AppLog.Write("Approving the build failed", e);
                return 1;
            }
        }

        if (parsed.RemoveSignIn)
        {
            try
            {
                new SignInStartup(new RegistryRunKeys(), Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mirador.exe")).TurnOff();
                return 0;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        if (IsElevated())
        {
            MessageBox("Mirador doesn't run as administrator: Monado would then turn away every game that isn't elevated too. Start Mirador normally; it asks for administrator rights itself when it needs them.");
            return 1;
        }

        SingleInstance? instance;
        try
        {
            instance = SingleInstance.TryAcquire();
        }
        catch (MiradorException e)
        {
            MessageBox(e.Message);
            return 1;
        }

        if (instance is null)
        {
            return AskTheFirst(parsed);
        }

        using (instance)
        {
            XamlCheckProcessRequirements();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(callback =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App(parsed);
            });
        }

        return 0;
    }

    private static int AskTheFirst(AppArgs parsed)
    {
        if ((parsed.Background || parsed.SignIn) && !parsed.Start)
        {
            return 0;
        }

        const uint AnyProcess = unchecked((uint)-1);
        AllowSetForegroundWindow(AnyProcess);
        var request = parsed.Start ? AppPipe.Start : AppPipe.Show;
        var reply = AppPipeClient.SendAsync(AppPipe.CurrentName(), request, parsed.Start ? StartWait : ShowWait, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (reply is null)
        {
            if (!parsed.Background)
            {
                MessageBox("Mirador is already running but didn't answer. Close it from the tray (or in Task Manager), then start it again.");
            }

            return 1;
        }

        return reply == AppPipe.Ok ? 0 : 1;
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    internal static void MessageBox(string text)
    {
        const uint IconError = 0x10;
        _ = MessageBoxW(IntPtr.Zero, text, "Mirador", IconError);
    }

    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
