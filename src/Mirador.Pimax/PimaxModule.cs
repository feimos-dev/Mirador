using System.Text;
using Mirador.Core;
using Mirador.Core.Modules;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Pimax;

public sealed record PimaxDeps(
    IRegistryReader Registry,
    GeometryUploader Geometry,
    DreamAirStateStore State,
    Func<string?> SteamVrDir,
    Func<DreamAirState, string> BlobVariant,
    Func<IReadOnlySet<UsbId>>? PresentDevices = null);

public sealed class PimaxModule(PimaxDeps deps) : IHeadsetModule
{
    public const string ModuleId = PimaxEnvironment.ModuleId;

    public static IReadOnlyList<EnvVar> FixedEnvironment { get; } =
        [.. PimaxEnvironment.DreamAirVariables.Select(v => new EnvVar(v.Key, v.Value))];

    public string Id => ModuleId;

    public string DisplayName => "Pimax";

    public bool UsesSteamVrLighthouse => Model().Lighthouse;

    public DirectModeKind DefaultDirectMode => DirectModeKind.Nvidia;

    public string? ElevatedModuleId => Model() == PimaxModels.DreamAir ? PimaxEnvironment.ModuleId : "";

    private volatile PimaxModel? model;

    public PimaxModel Model() => model ??= PimaxModels.DetectOrDreamAir(Present());

    public PimaxModel RefreshModel() => model = PimaxModels.DetectOrDreamAir(Present());

    private PimaxModel Remember(IReadOnlySet<UsbId> devices) => model = PimaxModels.DetectOrDreamAir(devices);

    public static IReadOnlyList<EnvVar> EnvironmentFor(PimaxModel model) => model.Lighthouse ? FixedEnvironment : [];

    public bool IsPresent(IReadOnlySet<UsbId> devices) =>
        PimaxModels.Detect(devices) is not null || devices.Contains(DreamAirIds.EyeTracker);

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
        var devices = Present(ctx);
        var choice = EyeVariantChoice.Of(profile);
        var model = WithChoice(Remember(devices), choice);
        var headsetThere = devices.Contains(model.Board) && (!model.Lighthouse || devices.Contains(DreamAirIds.Receiver));

        if (model.EyeSteps)
        {
            await DetectVariantAsync(ctx, ct).ConfigureAwait(false);
        }

        var state = LoadState();
        var variant = Variant(choice, state);

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

            if (RememberTracker(upload.TrackerSerial) && choice is null)
            {
                warnings.Add(new SessionWarning("geometry", DifferentHeadset));
            }
        }

        return new DreamAirSession(ctx, EnvironmentFor(model), upload, warnings, model.Name, headsetThere);
    }

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

    private static PimaxModel WithChoice(PimaxModel model, string? choice) =>
        choice == EyeVariantChoice.None ? model with { EyeSteps = false } : model;

    private string Variant(string? choice, DreamAirState state) =>
        choice is { } chosen && chosen != EyeVariantChoice.None ? chosen : deps.BlobVariant(state);

    private static bool ServiceRuns(IReadOnlyList<ProcInfo> running) =>
        running.Any(p => string.Equals(p.Name, PimaxNames.Service, StringComparison.OrdinalIgnoreCase));

    private static Task<HelperReply> StopPimaxAsync(ModuleContext ctx, IReadOnlyList<ProcInfo> running) =>
        ServiceRuns(running) || running.Any(p => PimaxNames.Processes.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            ? ctx.Helper.SendAsync(HelperCommand.StopPimax, CancellationToken.None)
            : Task.FromResult(new HelperReply(true));

    internal const string DifferentHeadset =
        "This looks like a different headset. Mirador reads its eye geometry from Pimax Play the next time Pimax runs.";

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
