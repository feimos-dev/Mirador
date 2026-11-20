using System.Text;
using Mirador.Core;
using Mirador.Core.Modules;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Pimax;

/// <summary>What the Dream Air module needs from the rest of Mirador.</summary>
/// <param name="Registry">The registry, to find Pimax Play.</param>
/// <param name="Geometry">Uploads the eye geometry.</param>
/// <param name="State">What Mirador remembers about the Dream Air.</param>
/// <param name="SteamVrDir">SteamVR's folder (<see cref="DreamAirChecks.FindSteamVr"/>).</param>
/// <param name="BlobVariant">The eye geometry variant Automatic uses for a state; <see cref="EyeBlobVariants.Fallback"/> until Mirador can tell.</param>
/// <param name="PresentDevices">The USB devices plugged in now, to tell the Pimax model apart; null: none known (the Dream Air's steps).</param>
public sealed record PimaxDeps(
    IRegistryReader Registry,
    GeometryUploader Geometry,
    DreamAirStateStore State,
    Func<string?> SteamVrDir,
    Func<DreamAirState, string> BlobVariant,
    Func<IReadOnlySet<UsbId>>? PresentDevices = null);

/// <summary>
/// The Pimax headsets: checks the PC, stops Pimax's software for the session, uploads the eye geometry, and gives Monado
/// the model's variables. Their displays go through NVIDIA direct mode, the profile's default layer.
/// </summary>
public sealed class PimaxModule(PimaxDeps deps) : IHeadsetModule
{
    /// <summary>The Pimax module (Mirador.md §4.11 item 53): every P3C headset, the steps each model has.</summary>
    public const string ModuleId = PimaxEnvironment.ModuleId;

    /// <summary>
    /// The Dream Air's own variables: the switch for SteamVR's Lighthouse driver. The one table lives in
    /// <see cref="PimaxEnvironment"/>, so the elevated path (the helper) writes the same one.
    /// </summary>
    public static IReadOnlyList<EnvVar> FixedEnvironment { get; } =
        [.. PimaxEnvironment.DreamAirVariables.Select(v => new EnvVar(v.Key, v.Value))];

    public string Id => ModuleId;

    public string DisplayName => "Pimax";

    /// <summary>The plugged-in model's tracking: SteamVR's Lighthouse driver for the Dream Airs, not for the Crystals.</summary>
    public bool UsesSteamVrLighthouse => Model().Lighthouse;

    /// <summary>Every Pimax display goes through NVIDIA direct mode.</summary>
    public DirectModeKind DefaultDirectMode => DirectModeKind.Nvidia;

    /// <summary>The helper's table for the Dream Air; the other models have no variables of their own.</summary>
    public string? ElevatedModuleId => Model() == PimaxModels.DreamAir ? PimaxEnvironment.ModuleId : "";

    private volatile PimaxModel? model;

    /// <summary>
    /// The model plugged in (with none, the Dream Air's steps): looked up once, then kept until <see cref="RefreshModel"/>
    /// (the devices changed) or a Start or check looks it up again. Listing USB devices takes a WMI query, and Home and
    /// the profile editor read this often.
    /// </summary>
    public PimaxModel Model() => model ??= PimaxModels.DetectOrDreamAir(Present());

    /// <summary>Looks the plugged-in model up again: call when the USB devices change.</summary>
    public PimaxModel RefreshModel() => model = PimaxModels.DetectOrDreamAir(Present());

    /// <summary>The model among <paramref name="devices"/>, kept as the current one.</summary>
    private PimaxModel Remember(IReadOnlySet<UsbId> devices) => model = PimaxModels.DetectOrDreamAir(devices);

    /// <summary>The variables a model's Monado gets from the module: the Lighthouse switch for a model that tracks with Lighthouse, else none.</summary>
    public static IReadOnlyList<EnvVar> EnvironmentFor(PimaxModel model) => model.Lighthouse ? FixedEnvironment : [];

    public bool IsPresent(IReadOnlySet<UsbId> devices) =>
        PimaxModels.Detect(devices) is not null || devices.Contains(DreamAirIds.EyeTracker);

    /// <summary>The checks of <see cref="DreamAirChecks"/>, and a warning while Automatic doesn't know the variant.</summary>
    public Task<IReadOnlyList<CheckResult>> CheckAsync(ModuleContext ctx, Profile profile, CancellationToken ct) =>
        Task.Run<IReadOnlyList<CheckResult>>(
            async () =>
            {
                var choice = EyeVariantChoice.Of(profile);
                var model = WithChoice(Remember(Present(ctx)), choice);
                if (model.EyeSteps)
                {
                    await DetectVariantAsync(ctx, ct).ConfigureAwait(false);
                }

                var state = LoadState();
                var variant = Variant(choice, state);
                List<CheckResult> results = [.. Checks(ctx).Run(variant, model)];
                if (!model.Tested)
                {
                    results.Add(new CheckResult("Headset", StatusLevel.Warning, $"The {model.Name} isn't tested with Mirador."));
                }

                if (model.EyeSteps && choice is null && BlobChoice.Resolve(state, null).NeedsOwner)
                {
                    results.Add(new CheckResult(
                        "Eye tracker variant",
                        StatusLevel.Warning,
                        $"Not known, using {variant}.",
                        "Open Pimax Play once, or pick it in the profile."));
                }

                return results;
            },
            ct);

    /// <summary>Stops Pimax's software, which holds the headset's display, for a direct-mode check.</summary>
    /// <exception cref="MiradorException">The helper couldn't stop it.</exception>
    public async Task FreeDisplayAsync(ModuleContext ctx, CancellationToken ct)
    {
        var stop = await StopPimaxAsync(ctx, ctx.Processes.Snapshot()).ConfigureAwait(false);
        if (!stop.Ok)
        {
            throw new MiradorException(stop.Error ?? "Mirador's helper service couldn't stop Pimax's software.");
        }
    }

    public async Task<ModuleSession> BeforeStartAsync(
        ModuleContext ctx, Profile profile, IProgress<string> progress, Action<IReadOnlyDictionary<string, string>> saveRecovery, CancellationToken ct)
    {
        // The model, once for this Start, and whether its board (and receiver) are there for Monado to find.
        var devices = Present(ctx);
        var choice = EyeVariantChoice.Of(profile);
        var model = WithChoice(Remember(devices), choice);
        var headsetThere = devices.Contains(model.Board) && (!model.Lighthouse || devices.Contains(DreamAirIds.Receiver));

        // Pimax's software still runs now: the last moment to read which geometry it uses.
        if (model.EyeSteps)
        {
            await DetectVariantAsync(ctx, ct).ConfigureAwait(false);
        }

        var state = LoadState();
        var variant = Variant(choice, state);

        // Everything that can refuse without changing the computer comes first.
        progress.Report("Checking the computer…");
        var checks = await Task.Run(() => Checks(ctx).Run(variant, model), ct).ConfigureAwait(false);
        if (checks.FirstOrDefault(c => c.Level == StatusLevel.Error) is { } failed)
        {
            throw new MiradorException(failed.Fix is null ? failed.Text : $"{failed.Text} {failed.Fix}");
        }

        var pimax = PimaxInstall.Locate(deps.Registry);
        if (pimax is null && model.EyeSteps)
        {
            throw new MiradorException("Pimax Play isn't installed. Install Pimax Play: Mirador uses its eye tracking runtime.");
        }

        ct.ThrowIfCancellationRequested();
        progress.Report("Stopping Pimax's software…");

        await FreeDisplayAsync(ctx, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var warnings = new List<SessionWarning>();
        GeometryResult? upload = null;
        if (model.EyeSteps)
        {
            progress.Report("Uploading the eye geometry…");
            upload = await deps.Geometry.UploadAsync(pimax!, variant, ct).ConfigureAwait(false);

            // Only Automatic uses what is remembered for the tracker.
            if (RememberTracker(upload.TrackerSerial) && choice is null)
            {
                warnings.Add(new SessionWarning("geometry", DifferentHeadset));
            }
        }

        return new DreamAirSession(ctx, EnvironmentFor(model), upload, warnings, model.Name, headsetThere);
    }

    /// <summary>Pimax's software is never started again, so a session left behind has nothing to undo.</summary>
    public Task RecoverAsync(ModuleContext ctx, IReadOnlyDictionary<string, string> data, CancellationToken ct) => Task.CompletedTask;

    public void AddDiagnostics(ModuleContext ctx, Profile profile, IDiagnosticsSink sink)
    {
        var text = new StringBuilder();
        var pimax = Safe(() => PimaxInstall.Locate(deps.Registry));
        text.Append("Pimax Play: ").AppendLine(pimax is null ? "not found" : $"{pimax.Version ?? "unknown version"} in {pimax.RuntimeDir}");
        text.Append("SteamVR: ").AppendLine(Safe(deps.SteamVrDir) ?? "not found");
        text.Append("Eye geometry variant: ").AppendLine(EyeVariantChoice.Of(profile) ?? $"automatic, {deps.BlobVariant(LoadState())}");
        sink.AddText("dreamair.txt", text.ToString());
        sink.AddFile("geometry-runtime.txt", Path.Combine(ctx.Paths.GeometryDir, "runtime.log"));
    }

    /// <summary><paramref name="model"/> without its eye steps when the profile's variant is None.</summary>
    private static PimaxModel WithChoice(PimaxModel model, string? choice) =>
        choice == EyeVariantChoice.None ? model with { EyeSteps = false } : model;

    /// <summary>The variant to upload: the profile's, or Automatic's for <paramref name="state"/>.</summary>
    private string Variant(string? choice, DreamAirState state) =>
        choice is { } chosen && chosen != EyeVariantChoice.None ? chosen : deps.BlobVariant(state);

    private static bool ServiceRuns(IReadOnlyList<ProcInfo> running) =>
        running.Any(p => string.Equals(p.Name, PimaxNames.Service, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Stops Pimax's service and processes through the helper. When none of them runs there is nothing to stop, so the
    /// helper isn't asked: a missing or broken helper then doesn't block a Start that doesn't need it.
    /// </summary>
    private static Task<HelperReply> StopPimaxAsync(ModuleContext ctx, IReadOnlyList<ProcInfo> running) =>
        ServiceRuns(running) || running.Any(p => PimaxNames.Processes.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            ? ctx.Helper.SendAsync(HelperCommand.StopPimax, CancellationToken.None)
            : Task.FromResult(new HelperReply(true));

    internal const string DifferentHeadset =
        "This looks like a different headset. Mirador reads its eye geometry from Pimax Play the next time Pimax runs.";

    /// <summary>
    /// Reads the eye geometry variant Pimax's eye runtime runs with, when it runs, and remembers it. A helper that
    /// can't answer leaves things as they were: the remembered or tested variant is used.
    /// </summary>
    private async Task DetectVariantAsync(ModuleContext ctx, CancellationToken ct)
    {
        IReadOnlyList<ProcInfo> running;
        try
        {
            running = ctx.Processes.Snapshot();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return;
        }

        if (!running.Any(p => string.Equals(p.Name, PimaxNames.EyeRuntimeProcess, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var reply = await ctx.Helper.SendAsync(HelperCommand.GetPimaxEyeBlob, ct).ConfigureAwait(false);
        if (!reply.Ok || reply.Value is not { } detected)
        {
            return;
        }

        try
        {
            deps.State.Update(s => BlobChoice.Resolve(s, detected).State);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Remembers the tracker's serial after an upload; whether that cleared a variant (a different headset).</summary>
    private bool RememberTracker(string? serial)
    {
        try
        {
            var cleared = false;
            deps.State.Update(s =>
            {
                var after = BlobChoice.AfterUpload(s, serial);
                cleared = BlobChoice.Cleared(s, after);
                return after;
            });
            return cleared;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private IReadOnlySet<UsbId> Present()
    {
        try
        {
            return deps.PresentDevices?.Invoke() ?? new HashSet<UsbId>();
        }
        catch (MiradorException)
        {
            return new HashSet<UsbId>();
        }
    }

    private static IReadOnlySet<UsbId> Present(ModuleContext ctx)
    {
        try
        {
            return ctx.Usb.Present();
        }
        catch (MiradorException)
        {
            return new HashSet<UsbId>();
        }
    }

    private DreamAirChecks Checks(ModuleContext ctx) =>
        new(deps.Registry, ctx.Usb, File.Exists, deps.SteamVrDir);

    /// <summary>The saved state; an empty one when the file can't be read now.</summary>
    private DreamAirState LoadState()
    {
        try
        {
            return deps.State.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DreamAirState();
        }
    }

    private static T? Safe<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is MiradorException or IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }
}
