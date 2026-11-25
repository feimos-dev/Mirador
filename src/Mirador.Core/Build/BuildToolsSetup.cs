namespace Mirador.Core.Build;

public interface IElevatedLauncher
{
    Task<int> RunAsync(CommandSpec spec, CancellationToken ct);
}

public sealed class ElevationDeclinedException() : Exception("The administrator prompt was declined.");

public sealed record BuildToolsReport(VsPlan VisualStudio, IReadOnlyList<(ToolPin Pin, ToolStatus Status)> Tools, ToolStatus Vcpkg)
{
    public IReadOnlyList<ToolStatus> Parts =>
        [new ToolStatus(VsPlan.Name, VisualStudio.State, VisualStudio.Detail), .. Tools.Select(t => t.Status), Vcpkg];

    public bool AllFound => Parts.All(p => p.State == ToolState.Found);

    public string? Problem()
    {
        var missing = Parts.Where(p => p.State == ToolState.Missing).Select(p => p.Name).ToList();
        var outdated = Parts.Where(p => p.State == ToolState.Fixable).Select(p => p.Name).ToList();
        var parts = new List<string>();
        if (missing.Count > 0)
        {
            parts.Add($"missing: {string.Join(", ", missing)}");
        }

        if (outdated.Count > 0)
        {
            parts.Add($"to update: {string.Join(", ", outdated)}");
        }

        return parts.Count == 0 ? null : $"Mirador's build tools aren't all ready ({string.Join("; ", parts)}).";
    }
}

public sealed class BuildToolsSetup(
    ICommandRunner runner,
    HttpClient http,
    IElevatedLauncher elevated,
    string toolsDir,
    string vcpkgCommit,
    string vswhere,
    string vsInstallerDir,
    Func<string, string?> signerOf,
    IReadOnlyList<ToolPin> pins)
{
    private readonly SemaphoreSlim preparing = new(1, 1);

    private volatile bool restartAsked;

    private const string RestartNeeded =
        "Visual Studio's installer wants Windows restarted before its new parts can be used: restart Windows, then build again.";

    public async Task<string?> PrepareAsync(Action<string> onLine, CancellationToken ct)
    {
        if (!preparing.Wait(0))
        {
            onLine("Another build is getting the build tools ready: waiting for it…");
            await preparing.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var report = await CheckAsync(ct).ConfigureAwait(false);
            if (report.AllFound)
            {
                return null;
            }

            if (restartAsked && report.Parts.All(p => p.State == ToolState.Found || p.Name == VsPlan.Name))
            {
                return RestartNeeded;
            }

            onLine($"Getting the build tools ready first: {string.Join(", ", report.Parts.Where(p => p.State != ToolState.Found).Select(p => p.Name))}.");
            var failures = await InstallMissingAsync(report, onLine, ct).ConfigureAwait(false);
            if (failures.Count > 0)
            {
                return $"{string.Join(' ', failures)} Build again to try once more.";
            }

            var after = await CheckAsync(ct).ConfigureAwait(false);
            if (after.Problem() is not { } problem)
            {
                return null;
            }

            var onlyVisualStudio = after.Parts.All(p => p.State == ToolState.Found || p.Name == VsPlan.Name);
            return restartAsked && onlyVisualStudio ? RestartNeeded
                : onlyVisualStudio ? "Visual Studio's installer finished, but its C++ tools don't show yet: open the Visual Studio Installer to check them, then build again."
                : $"{problem} Build again to try once more.";
        }
        finally
        {
            preparing.Release();
        }
    }

    public async Task<BuildToolsReport> CheckAsync(CancellationToken ct)
    {
        var vs = VsPlan.For(await VsWhere.FindAsync(runner, vswhere, ct).ConfigureAwait(false));
        var check = new BuildToolsCheck(toolsDir, vcpkgCommit);
        return new BuildToolsReport(vs, [.. pins.Select(p => (p, check.Tool(p)))], check.Vcpkg());
    }

    internal async Task<IReadOnlyList<string>> InstallMissingAsync(BuildToolsReport report, Action<string> onLine, CancellationToken ct)
    {
        var failures = new List<string>();
        var installer = new ToolsInstaller(http, runner, toolsDir, vcpkgCommit);
        async Task Try(Func<Task> step)
        {
            try
            {
                await step().ConfigureAwait(false);
            }
            catch (MiradorException e)
            {
                onLine(e.Message);
                failures.Add(e.Message);
            }
        }

        foreach (var (pin, status) in report.Tools.Where(t => t.Status.State != ToolState.Found))
        {
            await Try(() => installer.InstallAsync(pin, onLine, ct)).ConfigureAwait(false);
        }

        if (report.Vcpkg.State != ToolState.Found)
        {
            var git = Path.Combine(toolsDir, "git", "cmd", "git.exe");
            await Try(() => installer.InstallVcpkgAsync(File.Exists(git) ? git : "git", onLine, ct)).ConfigureAwait(false);
        }

        if (report.VisualStudio.State != ToolState.Found && !restartAsked)
        {
            await Try(() => InstallVisualStudioAsync(report.VisualStudio, onLine, ct)).ConfigureAwait(false);
        }

        return failures;
    }

    private async Task InstallVisualStudioAsync(VsPlan plan, Action<string> onLine, CancellationToken ct)
    {
        BootstrapperFile? bootstrapper = null;
        if (plan.State == ToolState.Missing)
        {
            onLine("Downloading Microsoft's Build Tools installer…");
            bootstrapper = await VsBootstrapper.DownloadAsync(http, Path.Combine(toolsDir, "download"), signerOf, ct).ConfigureAwait(false);
        }

        using (bootstrapper)
        {
            onLine(plan.State == ToolState.Missing
                ? "Installing Microsoft's Build Tools (Windows asks for an administrator; the installer shows its progress)…"
                : $"Adding {string.Join(" and ", plan.Missing.Select(VsComponents.Describe))} to {plan.Install!.Name} (Windows asks for an administrator)…");
            int code;
            try
            {
                code = await elevated.RunAsync(plan.Command(vsInstallerDir, bootstrapper?.Path ?? ""), ct).ConfigureAwait(false);
            }
            catch (ElevationDeclinedException)
            {
                throw new MiradorException("The administrator prompt was declined, so Visual Studio wasn't changed.");
            }

            switch (code)
            {
                case 0:
                    onLine("Visual Studio is ready.");
                    break;
                case 3010:
                    restartAsked = true;
                    onLine("Visual Studio is ready; Windows wants a restart before it is used.");
                    break;
                default:
                    throw new MiradorException($"Visual Studio's installer ended with code {code}: it may not have finished. Open the Visual Studio Installer to see what it says, or its logs in %TEMP% (dd_*.log).");
            }
        }
    }
}
