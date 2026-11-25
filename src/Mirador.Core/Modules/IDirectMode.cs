using Mirador.Core.Profiles;

namespace Mirador.Core.Modules;

public interface IDirectMode
{
    public const string HeadsetOnDesktopLabel = "Direct mode";

    DirectModeKind Kind { get; }

    string DisplayName { get; }

    Task<IReadOnlyList<CheckResult>> CheckAsync(ModuleContext ctx, CancellationToken ct);

    Task<string?> SetRegisteredAsync(ModuleContext ctx, bool used, CancellationToken ct);

    Task<ModuleSession> BeforeStartAsync(ModuleContext ctx, IProgress<string> progress, CancellationToken ct);

    Task<CheckResult> CheckDisplayAsync(ModuleContext ctx, CancellationToken ct);

    Task<string?> FixAsync(ModuleContext ctx, CancellationToken ct);

    void AddDiagnostics(ModuleContext ctx, IDiagnosticsSink sink);
}
