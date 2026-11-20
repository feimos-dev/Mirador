using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Profiles;
using Mirador.Core.Runtime;
using Mirador.Ipc;

namespace Mirador.Core.Session;

/// <summary>What a <see cref="SessionController"/> works with.</summary>
/// <param name="Profiles">The profiles; Start uses the selected one.</param>
/// <param name="MonadoSettings">The Monado settings every profile shares.</param>
/// <param name="Modules">The headset modules, by <see cref="IHeadsetModule.Id"/>.</param>
/// <param name="Context">What the modules may use; its clock times every wait of the session too.</param>
/// <param name="Locator">Finds the profile's Monado.</param>
/// <param name="Launcher">Starts <c>monado-service</c>. The app owns it for its whole life; the controller never disposes it.</param>
/// <param name="Pipe">Tells when Monado is ready for apps.</param>
/// <param name="Runtime">Tells whether the profile's Monado is the active OpenXR runtime.</param>
/// <param name="StandInExe">Mirador's own copy of the stand-in, copied into build folders.</param>
/// <param name="InheritedEnvironment">The environment <c>monado-service</c> starts from: the user's default one (<c>UserEnvironment.Default</c>), not Mirador's, which may be a game's.</param>
/// <param name="Hook">Told when a session starts and when it ends (the base stations); null: nobody.</param>
/// <param name="ElevatedConsented">Whether the current user allowed Monado to run as administrator; null: never (the job launcher is always used).</param>
/// <param name="ElevatedLauncher">Builds the launcher that starts Monado as administrator through the helper for the given settings; null: no elevated path.</param>
/// <param name="ApprovedBuildCurrent">Whether the profile's current build is the approved one; null: none is.</param>
/// <param name="ElevatedInstalledCopy">Whether this Mirador is the installed one the helper starts Monado as administrator for (asked once consent is on); null: never.</param>
/// <param name="DirectModes">The direct-mode layers, by <see cref="IDirectMode.Kind"/>; null: none.</param>
/// <param name="ControllersOffAtStop">Whether Monado turns its Lighthouse controllers and trackers off as it stops (<see cref="AppSettings.ControllersOffAtStop"/>); null: never.</param>
/// <param name="ElevationFixVerified">Whether Verify found the elevation fix in the profile's build as it is now (<see cref="ElevatedStart.ElevationFix"/>); null: never.</param>
/// <param name="VariablesApproved">Whether the profile's variables are exactly the ones approved to run as administrator; null: never.</param>
/// <param name="FolderBackups">Copies Monado's folder before each launch when it changed; null: no copies.</param>
/// <param name="OpenLibmonado">Opens libmonado from a Monado install's <c>bin</c> folder once Monado is up; null: <see cref="Libmonado.Open"/>.</param>
public sealed record SessionDeps(
    ProfileStore Profiles,
    MonadoSettingsStore MonadoSettings,
    IReadOnlyDictionary<string, IHeadsetModule> Modules,
    ModuleContext Context,
    MonadoLocator Locator,
    IProcessLauncher Launcher,
    IMonadoPipe Pipe,
    OpenXrRuntime Runtime,
    string StandInExe,
    IReadOnlyDictionary<string, string> InheritedEnvironment,
    ISessionHook? Hook = null,
    Func<bool>? ElevatedConsented = null,
    Func<ElevatedStart, IProcessLauncher>? ElevatedLauncher = null,
    Func<bool>? ElevatedInstalledCopy = null,
    Func<Profile, bool>? ApprovedBuildCurrent = null,
    IReadOnlyDictionary<DirectModeKind, IDirectMode>? DirectModes = null,
    Func<Profile, MonadoInstall, bool>? ElevationFixVerified = null,
    Func<bool>? ControllersOffAtStop = null,
    Func<string, LibmonadoOpen>? OpenLibmonado = null,
    MonadoFolderBackups? FolderBackups = null,
    Func<Profile, bool>? VariablesApproved = null)
{
    public Func<string, LibmonadoOpen> OpenLibmonado { get; init; } = OpenLibmonado ?? Libmonado.Open;
}
