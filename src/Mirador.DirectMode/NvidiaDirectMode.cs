using System.Text;
using Mirador.Core;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Native;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.DirectMode;

public sealed record NvidiaDirectModeDeps(
    string BundledLayerDir,
    Func<ProcessSpec, IConsoleProcess> StartConsole,
    DirectModeStateStore State,
    Func<string?> NvidiaWindowsVersion,
    Func<IReadOnlyList<MonitorEntry>>? Monitors = null,
    Func<bool>? MonadoRuns = null);

public sealed class NvidiaDirectMode(NvidiaDirectModeDeps deps) : IDirectMode
{
    private static readonly string[] Vendors = ["PVR"];

    public DirectModeKind Kind => DirectModeKind.Nvidia;

    public string DisplayName => "NVIDIA";

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

    private bool HeadsetShowsAsMonitor() =>
        deps.Monitors is { } monitors
        && Safe(() => deps.MonadoRuns?.Invoke() ?? false) is not true
        && Safe(DriverNow) is not null
        && LayerCheck().Level == StatusLevel.Ok
        && Safe(() => OnDesktop(monitors())) is true;

    private static bool OnDesktop(IReadOnlyList<MonitorEntry> monitors) =>
        Vendors.Any(vendor => HeadsetMonitor.HeadsetOnDesktop(monitors, vendor));

    private string? DriverNow() =>
        deps.NvidiaWindowsVersion() is { } windows ? NvidiaDriver.FromWindowsVersion(windows) ?? windows : null;

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
