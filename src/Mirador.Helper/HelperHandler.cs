using System.Reflection;
using System.Security.Principal;
using Mirador.Helper.DirectMode;
using Mirador.Helper.ElevatedMonado;
using Mirador.Ipc;

[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace Mirador.Helper;

public interface IServiceControl
{
    bool Exists(string name);

    bool IsRunning(string name);

    void Stop(string name, TimeSpan timeout);

    string? ImagePath(string name);

    (int Id, DateTime Started)? Process(string name);
}

public sealed record ProcessCommandLine(string? ExecutablePath, string CommandLine, int? ParentId = null, DateTime? Started = null);

public interface IProcessControl
{
    void KillAll(IEnumerable<string> names);

    IReadOnlyList<ProcessCommandLine> CommandLines(string processName);
}

public interface IRuntimeRegistry
{
    string? GetActive();

    void SetActive(string? path);

    string? GetRecorded();

    void SetRecorded(string? path);

    void SetAvailable(string manifest, bool available);
}

public sealed record CallerInfo(SecurityIdentifier Sid, int SessionId, int ProcessId, string? ImagePath);

public sealed class HelperHandler
{
    private const string BundledMissing = "The bundled Monado isn't installed yet. Install it on the Profiles page.";
    private const string NoMiradorsMonado = "This helper can't install the bundled Monado.";
    private const string NotApproved = "This profile's build isn't approved. Press Start to approve it.";
    private const string NoCaller = "Mirador's helper service couldn't tell who asked, so it changed nothing.";
    private const string NotInstalledMirador = "Only the installed Mirador may change which version of the bundled Monado is current.";
    private const string NoConsent = "Mirador's helper service has no way to reach the consent for Monado as administrator.";
    private const string NoElevated = "Mirador's helper service can't start Monado as administrator.";
    private const string NoStart = "Mirador's helper service was asked to start Monado as administrator without any settings.";
    private const string UnknownCommand = "Mirador's helper service doesn't know that command.";
    private const string NoLayers = "Mirador's helper service can't register the direct-mode layer.";
    private const int MaxErrorLength = 600;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);

    private readonly IServiceControl services;
    private readonly IProcessControl processes;
    private readonly IRuntimeRegistry registry;
    private readonly ElevatedConsent? consent;
    private readonly IElevatedMonado? elevated;
    private readonly string version;
    private readonly string installDir;
    private readonly IMonadoInstaller? monado;
    private readonly IApprovedBuildStore? approved;
    private readonly LayerRegistration? layers;
    private readonly object gate = new();

    public HelperHandler(
        IServiceControl services, IProcessControl processes, IRuntimeRegistry registry,
        string? version = null, ElevatedConsent? consent = null, IElevatedMonado? elevated = null, string? installDir = null,
        IMonadoInstaller? monado = null, IApprovedBuildStore? approved = null, LayerRegistration? layers = null)
    {
        this.layers = layers;
        this.monado = monado;
        this.approved = approved;
        this.services = services;
        this.processes = processes;
        this.registry = registry;
        this.consent = consent;
        this.elevated = elevated;
        this.version = version ?? OwnVersion();
        this.installDir = installDir ?? DefaultInstallDir;
    }

    public static string DefaultInstallDir { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));

    public Task<HelperReply> HandleAsync(HelperRequest request, CallerInfo? caller = null) =>
        Task.Run(() => Handle(request, caller));

    public Task<HelperReply> RestoreRuntimeAsync() => Task.Run(() => Locked(RestoreRuntime));

    private HelperReply Handle(HelperRequest request, CallerInfo? caller)
    {
        var command = request.Command;

        if (command == HelperCommand.Ping)
        {
            return new HelperReply(true, Value: version);
        }

        if (command == HelperCommand.GetInstallFolder)
        {
            return new HelperReply(true, Value: installDir);
        }

        if (command == HelperCommand.WaitMonadoElevated)
        {
            return WaitMonadoElevated(caller);
        }

        if (command == HelperCommand.StopMonadoElevated)
        {
            return StopMonadoElevated(caller, request.Clean == true);
        }

        if (command == HelperCommand.InstallMonado)
        {
            if (monado is null)
            {
                return new HelperReply(false, NoMiradorsMonado);
            }

            if (caller is null)
            {
                return new HelperReply(false, NoCaller);
            }

            if (!InstalledMirador.IsInstalledCopy(caller.ImagePath, installDir))
            {
                return new HelperReply(false, NotInstalledMirador);
            }

            try
            {
                return monado.InstallAsync(request.Version ?? "", CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                return new HelperReply(false, Shorten($"The bundled Monado couldn't be installed ({e.Message.TrimEnd('.')})."));
            }
        }

        return Locked(() => command switch
        {
            HelperCommand.SetRuntimeBundled => SetRuntimeBundled(),
            HelperCommand.StopPimax => StopPimax(),
            HelperCommand.GetPimaxEyeBlob => GetPimaxEyeBlob(),
            HelperCommand.RevokeElevatedMonado => RevokeElevatedMonado(caller),
            HelperCommand.StartMonadoElevated => StartMonadoElevated(request.Start, caller),
            HelperCommand.RollBackMonado =>
                caller is null ? new HelperReply(false, NoCaller)
                : !InstalledMirador.IsInstalledCopy(caller.ImagePath, installDir) ? new HelperReply(false, NotInstalledMirador)
                : monado?.RollBack() ?? new HelperReply(false, NoMiradorsMonado),
            HelperCommand.SetRuntimeApproved => SetRuntimeApproved(request.Build, caller),
            HelperCommand.ListApprovedBuilds => ListApprovedBuilds(caller),
            HelperCommand.ForgetApprovedBuild => ForgetApprovedBuild(request.Build, caller),
            HelperCommand.RegisterLayer => layers?.Register(request.Layer) ?? new HelperReply(false, NoLayers),
            HelperCommand.UnregisterLayer => layers?.Unregister(request.Layer) ?? new HelperReply(false, NoLayers),
            _ => new HelperReply(false, UnknownCommand),
        });
    }

    private HelperReply StartMonadoElevated(ElevatedStart? start, CallerInfo? caller)
    {
        if (caller is null)
        {
            return new HelperReply(false, NoCaller);
        }

        if (elevated is null)
        {
            return new HelperReply(false, NoElevated);
        }

        return start is null ? new HelperReply(false, NoStart) : elevated.Start(start, caller);
    }

    private HelperReply StopMonadoElevated(CallerInfo? caller, bool clean)
    {
        if (caller is null)
        {
            return new HelperReply(false, NoCaller);
        }

        return elevated is null ? new HelperReply(false, NoElevated) : elevated.Stop(caller, clean);
    }

    private HelperReply WaitMonadoElevated(CallerInfo? caller)
    {
        if (caller is null)
        {
            return new HelperReply(false, NoCaller);
        }

        return elevated is null ? new HelperReply(false, NoElevated) : elevated.Wait(caller);
    }

    private HelperReply Locked(Func<HelperReply> step)
    {
        lock (gate)
        {
            try
            {
                return step();
            }
            catch (Exception e)
            {
                return new HelperReply(false, Shorten(e.Message));
            }
        }
    }

    private HelperReply RevokeElevatedMonado(CallerInfo? caller)
    {
        if (caller is null)
        {
            return new HelperReply(false, NoCaller);
        }

        if (consent is null)
        {
            return new HelperReply(false, NoConsent);
        }

        consent.Revoke(caller.Sid);
        return new HelperReply(true);
    }

    private HelperReply ListApprovedBuilds(CallerInfo? caller) =>
        caller is null ? new HelperReply(false, NoCaller)
        : new HelperReply(true, Value: string.Join(',', approved?.ProfilesOf(caller.Sid.Value) ?? []));

    private HelperReply ForgetApprovedBuild(string? profileId, CallerInfo? caller)
    {
        if (caller is null)
        {
            return new HelperReply(false, NoCaller);
        }

        if (!ApprovedBuilds.IsProfileId(profileId))
        {
            return new HelperReply(false, NotApproved);
        }

        Forget(caller, profileId!);
        return new HelperReply(true);
    }

    private void Forget(CallerInfo caller, string profileId)
    {
        if (approved is null)
        {
            return;
        }

        var owner = caller.Sid.Value;
        if (approved.Get(owner, profileId) is { } build && registry.GetActive() is { } active
            && string.Equals(Path.GetFullPath(active), Path.GetFullPath(build.Manifest), StringComparison.OrdinalIgnoreCase))
        {
            if (monado?.CurrentManifest() is { } miradors && File.Exists(miradors))
            {
                registry.SetActive(miradors);
            }
            else
            {
                registry.SetActive(registry.GetRecorded() is { } recorded && File.Exists(recorded) ? recorded : null);
                registry.SetRecorded(null);
            }
        }

        ApprovedBuilds.Forget(owner, profileId, installDir, approved);
    }

    private HelperReply SetRuntimeBundled()
    {
        if (monado?.CurrentManifest() is not { } manifest || !File.Exists(manifest))
        {
            return new HelperReply(false, BundledMissing);
        }

        var active = registry.GetActive();
        if (active is not null && !IsBundled(active))
        {
            registry.SetRecorded(active);
        }

        registry.SetActive(manifest);
        return new HelperReply(true);
    }

    private HelperReply SetRuntimeApproved(string? profileId, CallerInfo? caller)
    {
        if (caller is null)
        {
            return new HelperReply(false, NoCaller);
        }

        if (approved is null || !ApprovedBuilds.IsProfileId(profileId) || approved.Get(caller.Sid.Value, profileId!) is not { } build)
        {
            return new HelperReply(false, NotApproved);
        }

        if (ApprovedBuilds.Verify(build, installDir) is { } problem)
        {
            return new HelperReply(false, problem);
        }

        var active = registry.GetActive();
        if (active is not null && !IsBundled(active))
        {
            registry.SetRecorded(active);
        }

        registry.SetActive(build.Manifest);
        return new HelperReply(true);
    }

    private HelperReply RestoreRuntime()
    {
        var recorded = registry.GetRecorded();
        if (recorded is null)
        {
            return new HelperReply(true);
        }

        var active = registry.GetActive();
        if (active is not null && IsBundled(active))
        {
            registry.SetActive(recorded);
        }

        registry.SetRecorded(null);
        return new HelperReply(true);
    }

    private HelperReply StopPimax()
    {
        string? error = null;
        if (services.Exists(PimaxNames.Service) && services.IsRunning(PimaxNames.Service))
        {
            try
            {
                services.Stop(PimaxNames.Service, StopTimeout);
            }
            catch (Exception e)
            {
                error = Shorten(e.Message);
            }
        }

        try
        {
            processes.KillAll(PimaxNames.Processes);
        }
        catch (Exception e)
        {
            error ??= Shorten(e.Message);
        }

        return new HelperReply(error is null, error);
    }

    private HelperReply GetPimaxEyeBlob()
    {
        if (PimaxFolder() is not { } folder || services.Process(PimaxNames.Service) is not { } service)
        {
            return new HelperReply(true);
        }

        var expected = Path.Combine(folder, "EyeTrackingServer", "platform_runtime", PimaxNames.EyeRuntimeProcess + ".exe");
        var variants = processes.CommandLines(PimaxNames.EyeRuntimeProcess)
            .Where(p => p.ParentId == service.Id && p.Started >= service.Started && SameFile(p.ExecutablePath, expected))
            .Select(p => PimaxEyeBlob.Parse(p.CommandLine))
            .OfType<string>()
            .Distinct()
            .ToList();
        return variants.Count == 1 ? new HelperReply(true, Value: variants[0]) : new HelperReply(true);
    }

    private string? PimaxFolder()
    {
        var program = ServiceImage.ProgramOf(services.ImagePath(PimaxNames.Service));
        try
        {
            var folder = program is null ? null : Path.GetDirectoryName(program);
            return folder is null || !Path.IsPathFullyQualified(folder)
                ? null
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool SameFile(string? file, string expected)
    {
        try
        {
            return file is not null && Path.IsPathFullyQualified(file)
                && string.Equals(Path.GetFullPath(file), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private bool IsBundled(string manifest) =>
        monado?.IsMiradors(manifest) == true
        || Path.GetFullPath(manifest).StartsWith(ApprovedBuilds.Root(installDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string message) =>
        message.Length <= MaxErrorLength ? message : message[..(MaxErrorLength - 3)] + "...";

    public static string OwnVersionNumber() => OwnVersion().Split('+')[0];

    private static string OwnVersion() =>
        typeof(HelperHandler).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}
