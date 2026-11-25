using Mirador.Core.Profiles;

namespace Mirador.Core.Modules;

public interface IHeadsetModule
{
    string Id { get; }

    string DisplayName { get; }

    bool UsesSteamVrLighthouse { get; }

    DirectModeKind DefaultDirectMode => DirectModeKind.None;

    Task FreeDisplayAsync(ModuleContext ctx, CancellationToken ct) => Task.CompletedTask;

    string? ElevatedModuleId => Id;

    bool IsPresent(IReadOnlySet<UsbId> devices);

    Task<IReadOnlyList<CheckResult>> CheckAsync(ModuleContext ctx, Profile profile, CancellationToken ct);

    Task<ModuleSession> BeforeStartAsync(
        ModuleContext ctx, Profile profile, IProgress<string> progress, Action<IReadOnlyDictionary<string, string>> saveRecovery, CancellationToken ct);

    Task RecoverAsync(ModuleContext ctx, IReadOnlyDictionary<string, string> data, CancellationToken ct);

    void AddDiagnostics(ModuleContext ctx, Profile profile, IDiagnosticsSink sink);
}
