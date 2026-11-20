using Mirador.Core.Profiles;

namespace Mirador.Core.Modules;

/// <summary>
/// A direct-mode layer: how Monado reaches a headset display that the GPU's driver keeps off the desktop. A profile
/// picks one (<see cref="Profile.DirectMode"/>), whatever its headset module.
/// </summary>
public interface IDirectMode
{
    /// <summary>The label of the check that finds the headset on the desktop, which "Fix direct mode" answers.</summary>
    public const string HeadsetOnDesktopLabel = "Direct mode";

    DirectModeKind Kind { get; }

    /// <summary>The layer's name for the user, such as "NVIDIA".</summary>
    string DisplayName { get; }

    /// <summary>Checks what the layer needs on the computer, for Home and before a Start, which refuses on an error.</summary>
    Task<IReadOnlyList<CheckResult>> CheckAsync(ModuleContext ctx, CancellationToken ct);

    /// <summary>
    /// Gets the layer ready for Monado, which starts right after, once the headset module's steps are done (they free the
    /// headset's display). The session gives Monado the layer's variables and shows the display's status line.
    /// </summary>
    /// <exception cref="MiradorException">Monado must not start; the message says why and what to do.</exception>
    Task<ModuleSession> BeforeStartAsync(ModuleContext ctx, IProgress<string> progress, CancellationToken ct);

    /// <summary>Checks that the headset's display works through the layer, while Monado is stopped and the display is free.</summary>
    Task<CheckResult> CheckDisplayAsync(ModuleContext ctx, CancellationToken ct);

    /// <summary>
    /// Takes the headset off the desktop again, while Monado is stopped and the display is free; null when that worked,
    /// else what went wrong and what to do.
    /// </summary>
    Task<string?> FixAsync(ModuleContext ctx, CancellationToken ct);

    /// <summary>Adds what the layer contributes to the diagnostics zip.</summary>
    void AddDiagnostics(ModuleContext ctx, IDiagnosticsSink sink);
}
