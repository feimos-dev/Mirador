using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Profiles;
using Mirador.Core.Runtime;
using Mirador.Ipc;

namespace Mirador.Core.Session;

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
