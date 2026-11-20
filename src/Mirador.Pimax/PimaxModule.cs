using System.Text;
using Mirador.Core;
using Mirador.Core.DirectMode;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Native;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Pimax;

/// <summary>What the Dream Air module needs from the rest of Mirador.</summary>
/// <param name="Registry">The registry, to find Pimax Play.</param>
/// <param name="StartConsole">Starts the display check (<see cref="ConsoleProcess.Start"/>).</param>
/// <param name="Geometry">Uploads the eye geometry.</param>
/// <param name="State">What Mirador remembers about the Dream Air.</param>
/// <param name="BundledLayerDir">The layer that comes with Mirador (<c>{app}\layer</c>).</param>
/// <param name="NvidiaWindowsVersion">The NVIDIA driver's Windows version (<see cref="NvidiaDriver.CurrentWindowsVersion"/>).</param>
/// <param name="SteamVrDir">SteamVR's folder (<see cref="DreamAirChecks.FindSteamVr"/>).</param>
/// <param name="BlobVariant">The eye geometry variant to use for a state; <see cref="EyeBlobVariants.Tested"/> until Mirador can tell.</param>
/// <param name="PresentDevices">The USB devices plugged in now, to tell the Pimax model apart; null: none known (the Dream Air's steps).</param>
/// <param name="Monitors">The monitors Windows lists (<see cref="HeadsetMonitor.List"/>), to see the headset on the desktop; null: not looked at.</param>
/// <param name="MonadoRuns">Whether Monado runs now; the headset on the desktop is only a problem while it doesn't. Null: it doesn't.</param>
public sealed record PimaxDeps(
    IRegistryReader Registry,
    Func<ProcessSpec, IConsoleProcess> StartConsole,
    GeometryUploader Geometry,
    DreamAirStateStore State,
    string BundledLayerDir,
    Func<string?> NvidiaWindowsVersion,
    Func<string?> SteamVrDir,
    Func<DreamAirState, string> BlobVariant,
    Func<IReadOnlySet<UsbId>>? PresentDevices = null,
    Func<IReadOnlyList<MonitorEntry>>? Monitors = null,
    Func<bool>? MonadoRuns = null);

/// <summary>
/// The Pimax Dream Air: checks the PC, stops Pimax's software for the session, checks direct mode when the NVIDIA
/// driver changed, uploads the eye geometry, and gives Monado the layer's variables.
/// </summary>
public sealed class PimaxModule(PimaxDeps deps) : IHeadsetModule
{
    /// <summary>The Pimax module (Mirador.md §4.11 item 53): every P3C headset, the steps each model has.</summary>
    public const string ModuleId = PimaxEnvironment.ModuleId;

    /// <summary>
    /// The Dream Air's own variables: the switch for SteamVR's Lighthouse driver. The layer's variables the session
    /// adds, since the module uses direct mode (<see cref="DirectModeLayer.Environment"/>). The one table lives in
    /// <see cref="DreamAirEnvironment"/>, so the elevated path (the helper) writes the same one.
    /// </summary>
    public static IReadOnlyList<EnvVar> FixedEnvironment { get; } =
        [.. DreamAirEnvironment.ModuleVariables.Select(v => new EnvVar(v.Key, v.Value))];

    /// <summary>The label of the check that finds the headset on the desktop; Home and Diagnostics offer the fix beside it.</summary>
    public const string DirectModeOffLabel = "Direct mode";

    public string Id => ModuleId;

    public string DisplayName => "Pimax";

    /// <summary>The plugged-in model's tracking: SteamVR's Lighthouse driver for the Dream Airs, not for the Crystals.</summary>
    public bool UsesSteamVrLighthouse => Model().Lighthouse;

    /// <summary>Every Pimax display goes through NVIDIA direct mode: the session turns the layer on.</summary>
    public bool UsesNvidiaDirectMode => true;

    /// <summary>The helper has the Dream Air's table only: another model runs as the user.</summary>
    public string? ElevatedModuleId => Model() == PimaxModels.DreamAir ? PimaxEnvironment.ModuleId : null;

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

    /// <summary>The checks of <see cref="DreamAirChecks"/>, the layer's, and a warning while the headset's variant isn't known.</summary>
    public Task<IReadOnlyList<CheckResult>> CheckAsync(ModuleContext ctx, Profile profile, CancellationToken ct) =>
        Task.Run<IReadOnlyList<CheckResult>>(
            async () =>
            {
                var model = Remember(Present(ctx));
                if (model.EyeSteps)
                {
                    await DetectVariantAsync(ctx, ct).ConfigureAwait(false);
                }

                var state = LoadState();
                var variant = deps.BlobVariant(state);
                List<CheckResult> results = [.. Checks(ctx).Run(variant, model), LayerCheck()];
                if (!model.Tested)
                {
                    results.Add(new CheckResult(
                        "Headset", StatusLevel.Warning,
                        $"The {model.Name} isn't tested with Mirador: its display gets the mode its EDID asks for, and Monado needs a driver for it.",
                        "Use a profile whose Monado has a driver for it, and report how it went."));
                }

                if (HeadsetShowsAsMonitor())
                {
                    results.Add(new CheckResult(
                        DirectModeOffLabel, StatusLevel.Warning,
                        "The headset shows as a normal monitor, so Monado can't use it. Fix direct mode turns it back into a headset display."));
                }

                if (model.EyeSteps && BlobChoice.Resolve(state, null).NeedsOwner)
                {
                    results.Add(new CheckResult(
                        "Headset variant",
                        StatusLevel.Warning,
                        $"Not known yet: Mirador uploads the eye geometry of {variant}, the tested one.",
                        "Pick yours in Eye tracking, or start Pimax Play once and Mirador reads it."));
                }

                return results;
            },
            ct);

    /// <summary>
    /// Whether the headset is listed among the desktop's monitors while Monado doesn't run: direct mode is off for it
    /// (Pimax Play or SteamVR handed it back to Windows). A list that can't be read counts as no, and so does a PC where
    /// Fix direct mode can't help: without an NVIDIA GPU or Mirador's layer (their own checks say so).
    /// </summary>
    private bool HeadsetShowsAsMonitor()
    {
        if (deps.Monitors is null || Safe(() => deps.MonadoRuns?.Invoke() ?? false) || Safe(NvidiaDriverNow) is null
            || LayerCheck().Level != StatusLevel.Ok)
        {
            return false;
        }

        return Safe(() => HeadsetMonitor.HeadsetOnDesktop(deps.Monitors())) is true;
    }

    /// <summary>
    /// "Fix direct mode" (run it through <c>SessionController.RunWhileStoppedAsync</c>): stops Pimax's software, which
    /// has the headset on the desktop, and runs <c>mirador-display-check --enable-direct-mode</c>.
    /// </summary>
    /// <returns>Null when direct mode is on again; otherwise the problem, with the check's advice, in its own words.</returns>
    public async Task<string?> FixDirectModeAsync(ModuleContext ctx, CancellationToken ct)
    {
        LayerFiles layer;
        try
        {
            layer = DirectModeLayer.Locate(deps.BundledLayerDir);
        }
        catch (MiradorException e)
        {
            return e.Message;
        }

        return await AfterPimaxStoppedAsync<string?>(
            ctx,
            async () =>
            {
                var check = await DisplayCheck.RunAsync(layer.CheckExe, deps.StartConsole, ct, "--enable-direct-mode").ConfigureAwait(false);
                return check.Pass ? null : check.WhatToDo is { } advice
                    ? $"{DirectModeCheck.Sentence(check.Message)} {DirectModeCheck.Sentence(DirectModeCheck.Capitalized(advice))}"
                    : DirectModeCheck.Sentence(check.Message);
            },
            stop => stop.Error ?? "Pimax's software couldn't be stopped.").ConfigureAwait(false);
    }

    /// <summary>
    /// Stops Pimax's software, then runs <paramref name="run"/>; when the stop failed, <paramref name="stopFailed"/> gives
    /// the result instead.
    /// </summary>
    private static async Task<T> AfterPimaxStoppedAsync<T>(ModuleContext ctx, Func<Task<T>> run, Func<HelperReply, T> stopFailed)
    {
        var stop = await StopPimaxAsync(ctx, ctx.Processes.Snapshot()).ConfigureAwait(false);
        return stop.Ok ? await run().ConfigureAwait(false) : stopFailed(stop);
    }

    public async Task<ModuleSession> BeforeStartAsync(
        ModuleContext ctx, Profile profile, IProgress<string> progress, Action<IReadOnlyDictionary<string, string>> saveRecovery, CancellationToken ct)
    {
        // The model, once for this Start, and whether its board (and receiver) are there for Monado to find.
        var devices = Present(ctx);
        var model = Remember(devices);
        var headsetThere = devices.Contains(model.Board) && (!model.Lighthouse || devices.Contains(DreamAirIds.Receiver));

        // Pimax's software still runs now: the last moment to read which geometry it uses.
        if (model.EyeSteps)
        {
            await DetectVariantAsync(ctx, ct).ConfigureAwait(false);
        }

        var state = LoadState();
        var variant = deps.BlobVariant(state);

        // Everything that can refuse without changing the computer comes first.
        progress.Report("Checking the computer…");
        var checks = await Task.Run(() => Checks(ctx).Run(variant, model), ct).ConfigureAwait(false);
        if (checks.FirstOrDefault(c => c.Level == StatusLevel.Error) is { } failed)
        {
            throw new MiradorException(failed.Fix is null ? failed.Text : $"{failed.Text} {failed.Fix}");
        }

        var layer = DirectModeLayer.Locate(deps.BundledLayerDir);
        var pimax = PimaxInstall.Locate(deps.Registry);
        if (pimax is null && model.EyeSteps)
        {
            throw new MiradorException("Pimax Play isn't installed. Install Pimax Play: Mirador uses its eye tracking runtime.");
        }

        ct.ThrowIfCancellationRequested();
        progress.Report("Stopping Pimax's software…");

        var stop = await StopPimaxAsync(ctx, ctx.Processes.Snapshot()).ConfigureAwait(false);
        if (!stop.Ok)
        {
            throw new MiradorException(stop.Error ?? "Mirador's helper service couldn't stop Pimax's software.");
        }

        ct.ThrowIfCancellationRequested();

        // Pimax's software is stopped: the headset is free for the core's re-check after a driver update.
        var notUsed = await new DirectModeCheck(NvidiaDriverNow, deps.StartConsole).EnsureAsync(
            layer, state.LastGoodDriver, RememberDriver, check => SaveCheckOutput(ctx, check), progress, ct).ConfigureAwait(false);

        var warnings = new List<SessionWarning>();
        if (notUsed is not null)
        {
            warnings.Add(new SessionWarning("direct-mode", notUsed));
        }
        GeometryResult? upload = null;
        if (model.EyeSteps)
        {
            progress.Report("Uploading the eye geometry…");
            upload = await deps.Geometry.UploadAsync(pimax!, variant, ct).ConfigureAwait(false);

            if (RememberTracker(upload.TrackerSerial))
            {
                warnings.Add(new SessionWarning("geometry", DifferentHeadset));
            }
        }

        return new DreamAirSession(ctx, EnvironmentFor(model), upload, warnings, model.Name, headsetThere, layerUsed: notUsed is null);
    }

    /// <summary>Pimax's software is never started again, so a session left behind has nothing to undo.</summary>
    public Task RecoverAsync(ModuleContext ctx, IReadOnlyDictionary<string, string> data, CancellationToken ct) => Task.CompletedTask;

    public void AddDiagnostics(ModuleContext ctx, Profile profile, IDiagnosticsSink sink)
    {
        var text = new StringBuilder();
        text.Append("NVIDIA driver: ").AppendLine(Safe(() => NvidiaDriverNow()) ?? "none");
        var pimax = Safe(() => PimaxInstall.Locate(deps.Registry));
        text.Append("Pimax Play: ").AppendLine(pimax is null ? "not found" : $"{pimax.Version ?? "unknown version"} in {pimax.RuntimeDir}");
        text.Append("SteamVR: ").AppendLine(Safe(deps.SteamVrDir) ?? "not found");
        text.Append("Vulkan layer: ").AppendLine($"comes with Mirador ({deps.BundledLayerDir})");
        var state = LoadState();
        text.Append("Last good driver: ").AppendLine(state.LastGoodDriver ?? "none yet");
        text.Append("Eye geometry variant: ").AppendLine(deps.BlobVariant(state));
        sink.AddText("dreamair.txt", text.ToString());
        sink.AddFile("display-check.txt", DisplayCheckFile(ctx.Paths));
        sink.AddFile("geometry-runtime.txt", Path.Combine(ctx.Paths.GeometryDir, "runtime.log"));
    }

    /// <summary>
    /// The Diagnostics page's "Check display", while Monado is stopped (run it through
    /// <c>SessionController.RunWhileStoppedAsync</c>, which keeps Starts out meanwhile): stops Pimax's software (the
    /// check refuses while it holds the headset) and runs the display check. A pass records the driver as good, so the
    /// next Start doesn't check again.
    /// </summary>
    public async Task<DisplayCheckResult> CheckDisplayAsync(ModuleContext ctx, Profile profile, CancellationToken ct)
    {
        LayerFiles layer;
        try
        {
            layer = DirectModeLayer.Locate(deps.BundledLayerDir);
        }
        catch (MiradorException e)
        {
            return new DisplayCheckResult(false, e.Message, null, "") { Finished = false };
        }

        var stopFailed = false;
        var check = await AfterPimaxStoppedAsync(
            ctx,
            () => DisplayCheck.RunAsync(layer.CheckExe, deps.StartConsole, ct),
            stop =>
            {
                stopFailed = true;
                return new DisplayCheckResult(false, stop.Error ?? "Pimax's software couldn't be stopped.", null, "") { Finished = false };
            }).ConfigureAwait(false);

        if (!stopFailed)
        {
            SaveCheckOutput(ctx, check);
        }

        if (check.Pass && Safe(NvidiaDriverNow) is { } driver)
        {
            RememberDriver(driver);
        }

        return check;
    }

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

    /// <summary>Where the last display check's output is kept, for the diagnostics.</summary>
    public static string DisplayCheckFile(AppPaths paths) => Path.Combine(paths.LogsDir, "display-check.txt");

    /// <summary>Keeps the driver direct mode last passed with; a failure to save only costs a display check next time.</summary>
    private void RememberDriver(string? driver)
    {
        try
        {
            deps.State.Update(s => s with { LastGoodDriver = driver });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private DreamAirChecks Checks(ModuleContext ctx) =>
        new(deps.Registry, ctx.Usb, File.Exists, deps.NvidiaWindowsVersion, deps.SteamVrDir);

    private CheckResult LayerCheck()
    {
        const string Label = "Vulkan layer";
        try
        {
            DirectModeLayer.Locate(deps.BundledLayerDir);
            return new CheckResult(Label, StatusLevel.Ok, "Comes with Mirador");
        }
        catch (MiradorException e)
        {
            return new CheckResult(Label, StatusLevel.Error, e.Message);
        }
    }

    /// <summary>NVIDIA's version of the driver, or Windows' when Mirador can't read it; null without an NVIDIA GPU.</summary>
    private string? NvidiaDriverNow() =>
        deps.NvidiaWindowsVersion() is { } windows ? NvidiaDriver.FromWindowsVersion(windows) ?? windows : null;

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

    private static void SaveCheckOutput(ModuleContext ctx, DisplayCheckResult check)
    {
        try
        {
            var file = DisplayCheckFile(ctx.Paths);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, check.Output);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary><paramref name="s"/> ending in a full stop, so that two of them read as two sentences.</summary>
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
