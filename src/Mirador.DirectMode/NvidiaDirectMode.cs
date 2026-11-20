using System.Text;
using Mirador.Core;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Native;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.DirectMode;

/// <summary>What the NVIDIA direct-mode layer needs from the rest of Mirador.</summary>
/// <param name="BundledLayerDir">The layer that comes with Mirador (<c>{app}\layer</c>).</param>
/// <param name="StartConsole">Starts the display check (<see cref="ConsoleProcess.Start"/>).</param>
/// <param name="State">What Mirador remembers about direct mode.</param>
/// <param name="NvidiaWindowsVersion">The NVIDIA driver's Windows version (<see cref="NvidiaDriver.CurrentWindowsVersion"/>); null without an NVIDIA GPU.</param>
/// <param name="Monitors">The monitors Windows lists (<see cref="HeadsetMonitor.List"/>), to see the headset on the desktop; null: not looked at.</param>
/// <param name="MonadoRuns">Whether Monado runs now; the headset on the desktop is only a problem while it doesn't. Null: it doesn't.</param>
public sealed record NvidiaDirectModeDeps(
    string BundledLayerDir,
    Func<ProcessSpec, IConsoleProcess> StartConsole,
    DirectModeStateStore State,
    Func<string?> NvidiaWindowsVersion,
    Func<IReadOnlyList<MonitorEntry>>? Monitors = null,
    Func<bool>? MonadoRuns = null);

/// <summary>
/// NVIDIA direct mode through Mirador's layer: checks the GPU and the layer, has the helper register the layer, checks
/// direct mode again when the driver changed, and gives Monado the layer's variables.
/// </summary>
public sealed class NvidiaDirectMode(NvidiaDirectModeDeps deps) : IDirectMode
{
    /// <summary>The EDID vendors of the headsets the layer drives (its product table).</summary>
    private static readonly string[] Vendors = ["PVR"];

    public DirectModeKind Kind => DirectModeKind.Nvidia;

    public string DisplayName => "NVIDIA";

    /// <summary>Where the last display check's output is kept, for the diagnostics.</summary>
    public static string DisplayCheckFile(AppPaths paths) => Path.Combine(paths.LogsDir, "display-check.txt");

    public Task<IReadOnlyList<CheckResult>> CheckAsync(ModuleContext ctx, CancellationToken ct) =>
        Task.Run<IReadOnlyList<CheckResult>>(
            () =>
            {
                List<CheckResult> results = [GpuCheck(), LayerCheck()];
                if (HeadsetShowsAsMonitor())
                {
                    results.Add(new CheckResult(
                        IDirectMode.HeadsetOnDesktopLabel, StatusLevel.Warning,
                        "The headset shows as a normal monitor, so Monado can't use it. Fix direct mode turns it back into a headset display."));
                }

                return results;
            },
            ct);

    public async Task<string?> SetRegisteredAsync(ModuleContext ctx, bool used, CancellationToken ct)
    {
        var request = new HelperRequest(used ? HelperCommand.RegisterLayer : HelperCommand.UnregisterLayer, Layer: DirectModeEnvironment.Nvidia);
        var reply = await ctx.Helper.SendAsync(request, ct).ConfigureAwait(false);
        return reply.Ok ? null : reply.Error ?? "Mirador's helper service couldn't change the Vulkan layers.";
    }

    /// <summary>
    /// Registers the layer (again: profiles are per Windows user, the registration per machine, so another user may have
    /// taken it out), checks direct mode when the driver changed, and gives Monado the layer's variables.
    /// </summary>
    public async Task<ModuleSession> BeforeStartAsync(ModuleContext ctx, IProgress<string> progress, CancellationToken ct)
    {
        var layer = DirectModeLayer.Locate(deps.BundledLayerDir);
        if (await SetRegisteredAsync(ctx, used: true, ct).ConfigureAwait(false) is { } problem)
        {
            throw new MiradorException(problem);
        }

        var notUsed = await new DirectModeCheck(DriverNow, deps.StartConsole).EnsureAsync(
            layer, LoadState().LastGoodDriver, RememberDriver, check => SaveCheckOutput(ctx, check), progress, ct).ConfigureAwait(false);
        return new DirectModeSession(DirectModeLayer.Environment(deps.BundledLayerDir), notUsed, ctx.Time);
    }

    /// <summary>Runs the display check. A pass records the driver as good, so the next Start doesn't check again.</summary>
    public async Task<CheckResult> CheckDisplayAsync(ModuleContext ctx, CancellationToken ct)
    {
        const string Label = "Display check";
        LayerFiles layer;
        try
        {
            layer = DirectModeLayer.Locate(deps.BundledLayerDir);
        }
        catch (MiradorException e)
        {
            return new CheckResult(Label, StatusLevel.Error, e.Message);
        }

        var check = await DisplayCheck.RunAsync(layer.CheckExe, deps.StartConsole, ct).ConfigureAwait(false);
        SaveCheckOutput(ctx, check);
        if (check.Pass && Safe(DriverNow) is { } driver)
        {
            RememberDriver(driver);
        }

        var message = DirectModeCheck.Sentence(check.Message);
        return check.Pass
            ? new CheckResult(Label, StatusLevel.Ok, check.Message == "Direct mode works." ? check.Message : $"Direct mode works: {message}")
            : new CheckResult(
                Label,
                StatusLevel.Error,
                check.Finished
                    ? $"Direct mode doesn't work: {message}{(check.WhatToDo is null ? "" : " What to do: " + DirectModeCheck.Sentence(check.WhatToDo))}"
                    : message);
    }

    /// <summary>Runs <c>mirador-display-check --enable-direct-mode</c>, then looks whether the headset left the desktop.</summary>
    public async Task<string?> FixAsync(ModuleContext ctx, CancellationToken ct)
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

        var check = await DisplayCheck.RunAsync(layer.CheckExe, deps.StartConsole, ct, "--enable-direct-mode").ConfigureAwait(false);
        if (!check.Pass)
        {
            return check.WhatToDo is { } advice
                ? $"{DirectModeCheck.Sentence(check.Message)} {DirectModeCheck.Sentence(DirectModeCheck.Capitalized(advice))}"
                : DirectModeCheck.Sentence(check.Message);
        }

        return deps.Monitors is { } monitors && Safe(() => OnDesktop(monitors())) is true
            ? "Direct mode was turned on, but the headset still shows as a monitor. Unplug and plug in the headset's DisplayPort cable, then try again."
            : null;
    }

    public void AddDiagnostics(ModuleContext ctx, IDiagnosticsSink sink)
    {
        var text = new StringBuilder();
        text.Append("NVIDIA driver: ").AppendLine(Safe(DriverNow) ?? "none");
        text.Append("Last good driver: ").AppendLine(LoadState().LastGoodDriver ?? "none yet");
        text.Append("Vulkan layer: ").AppendLine(deps.BundledLayerDir);
        sink.AddText("directmode.txt", text.ToString());
        sink.AddFile("display-check.txt", DisplayCheckFile(ctx.Paths));
    }

    private CheckResult GpuCheck()
    {
        const string Label = "NVIDIA GPU";
        try
        {
            return deps.NvidiaWindowsVersion() is { } windows
                ? new(Label, StatusLevel.Ok, $"Driver {NvidiaDriver.FromWindowsVersion(windows) ?? windows}")
                : new(Label, StatusLevel.Error, "No NVIDIA GPU found.", "Set the profile's direct-mode layer to None.");
        }
        catch (MiradorException e)
        {
            return new(Label, StatusLevel.Error, e.Message);
        }
    }

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

    /// <summary>
    /// Whether the headset is listed among the desktop's monitors while Monado doesn't run: direct mode is off for it
    /// (its own software or SteamVR handed it back to Windows). A list that can't be read counts as no, and so does a PC
    /// where Fix direct mode can't help: without an NVIDIA GPU or Mirador's layer (their own checks say so).
    /// </summary>
    private bool HeadsetShowsAsMonitor() =>
        deps.Monitors is { } monitors
        && Safe(() => deps.MonadoRuns?.Invoke() ?? false) is not true
        && Safe(DriverNow) is not null
        && LayerCheck().Level == StatusLevel.Ok
        && Safe(() => OnDesktop(monitors())) is true;

    private static bool OnDesktop(IReadOnlyList<MonitorEntry> monitors) =>
        Vendors.Any(vendor => HeadsetMonitor.HeadsetOnDesktop(monitors, vendor));

    /// <summary>NVIDIA's version of the driver, or Windows' when Mirador can't read it; null without an NVIDIA GPU.</summary>
    private string? DriverNow() =>
        deps.NvidiaWindowsVersion() is { } windows ? NvidiaDriver.FromWindowsVersion(windows) ?? windows : null;

    /// <summary>Keeps the driver direct mode last passed with; a failure to save only costs a display check next time.</summary>
    private void RememberDriver(string? driver)
    {
        try
        {
            deps.State.Save(LoadState() with { LastGoodDriver = driver });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The saved state; an empty one when the file can't be read now.</summary>
    private DirectModeState LoadState()
    {
        try
        {
            return deps.State.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DirectModeState();
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
