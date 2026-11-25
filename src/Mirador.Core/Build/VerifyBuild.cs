using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mirador.Core.Monado;

namespace Mirador.Core.Build;

[JsonConverter(typeof(JsonStringEnumConverter<VerifyOutcome>))]
public enum VerifyOutcome
{
    Passed,
    Failed,
    Skipped,
}

public sealed record VerifyStep(string Name, VerifyOutcome Outcome, string Detail);

public sealed record VerifyResult(
    string Key, DateTimeOffset At, IReadOnlyList<VerifyStep> Steps, bool? HasElevationFix = null, string? LogFile = null, bool StrictPipe = false)
{
    public bool HasFailure() => Steps.Any(s => s.Outcome == VerifyOutcome.Failed);

    public static (string ProfileId, VerifyResult Result)? ForDiagnostics(
        IEnumerable<(string ProfileId, VerifyResult? Result)> results, string? wanted)
    {
        var known = results.Where(r => r.Result is not null).Select(r => (r.ProfileId, Result: r.Result!)).ToList();
        foreach (var r in known)
        {
            if (r.ProfileId == wanted)
            {
                return r;
            }
        }

        return known.Count == 0 ? null : known.OrderByDescending(r => r.Result.At).First();
    }

    public string Verdict(string when)
    {
        var skipped = Steps.Count(s => s.Outcome == VerifyOutcome.Skipped);
        return HasFailure() ? $"Failed on {when}: Diagnostics has what went wrong."
            : skipped > 0 ? $"Passed on {when}, with {skipped} {(skipped == 1 ? "check" : "checks")} skipped: Diagnostics says why."
            : $"Passed on {when}.";
    }

    public string Summary()
    {
        var failed = Steps.Where(s => s.Outcome == VerifyOutcome.Failed).Select(s => s.Name).ToList();
        var skipped = Steps.Count(s => s.Outcome == VerifyOutcome.Skipped);
        return failed.Count > 0 ? $"Failed: {string.Join("; ", failed)}."
            : skipped > 0 ? $"Every check that ran passed; {skipped} skipped."
            : "Every check passed.";
    }

    public void Save(string file) => JsonFile.Save(file, this, VerifyJsonContext.Default.VerifyResult);

    public static VerifyResult? Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), VerifyJsonContext.Default.VerifyResult) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

public sealed record XrCheckReport(string Api, bool Ok, int Frames, string Runtime, string System, string Error, int Submitted = -1)
{
    public const string Prefix = "XR-CHECK ";

    public static XrCheckReport? Parse(IEnumerable<string> lines)
    {
        var line = lines.LastOrDefault(l => l.StartsWith(Prefix, StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(line[Prefix.Length..], VerifyJsonContext.Default.XrCheckReport);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record ElevatedVerify(IAsyncDisposable? Running, string? WhyNot, bool Failed = false, bool PipeRelaxed = false);

public interface IVerifyServices
{
    Task<IAsyncDisposable> StartHeadlessAsync(MonadoInstall install, CancellationToken ct);

    StandInWait ExpectStandIn(MonadoInstall install);

    Task<ElevatedVerify> StartElevatedHeadlessAsync(MonadoInstall install, CancellationToken ct);
}

public sealed class StandInWait(MonadoInstall install, Func<CancellationToken, Task<IAsyncDisposable>> start) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IAsyncDisposable? started;
    private bool ended;

    public MonadoInstall Install { get; } = install;

    public bool WasAsked { get; private set; }

    public string? Failure { get; private set; }

    public Action? Ended { get; set; }

    public async Task StartAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ended)
            {
                throw new MiradorException("Verify isn't waiting for a game any more.");
            }

            WasAsked = true;
            if (started is null)
            {
                try
                {
                    started = await start(ct).ConfigureAwait(false);
                }
                catch (MiradorException e)
                {
                    Failure = e.Message;
                    throw;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Ended?.Invoke();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ended = true;
            if (started is not null)
            {
                await started.DisposeAsync().ConfigureAwait(false);
                started = null;
            }
        }
        finally
        {
            gate.Release();
        }
    }
}

public sealed class VerifyBuild(ICommandRunner runner, string xrCheck, TimeProvider time)
{
    public const string D3D11Step = "A session in D3D11";
    public const string VulkanStep = "A session in Vulkan";
    public const string StandInStep = "A game's start through the stand-in";
    public const string ElevatedStep = "Monado as administrator, the game as you";

    public const int Frames = 300;

    public const int ShortFrames = 90;

    public const int ClientTimeoutSeconds = 120;

    public async Task<VerifyResult> RunAsync(MonadoInstall install, string key, IVerifyServices services, Action<string> onLine, CancellationToken ct)
    {
        var steps = new List<VerifyStep>();

        onLine("Starting Monado with its null compositor and a simulated headset…");
        IAsyncDisposable? headless = null;
        try
        {
            headless = await services.StartHeadlessAsync(install, ct).ConfigureAwait(false);
        }
        catch (MiradorException e)
        {
            var why = "Monado didn't start headless: " + e.Message;
            steps.Add(new VerifyStep(D3D11Step, VerifyOutcome.Failed, why));
            steps.Add(new VerifyStep(VulkanStep, VerifyOutcome.Failed, why));
        }

        if (headless is not null)
        {
            await using (headless.ConfigureAwait(false))
            {
                steps.Add(await SessionStepAsync(D3D11Step, "d3d11", Frames, install, onLine, ct).ConfigureAwait(false));
                ct.ThrowIfCancellationRequested();
                steps.Add(await SessionStepAsync(VulkanStep, "vulkan", Frames, install, onLine, ct).ConfigureAwait(false));
                ct.ThrowIfCancellationRequested();
            }
        }

        var standIn = StandIn.FileNames.Select(name => Path.Combine(install.StandInDir, name)).FirstOrDefault(File.Exists);
        if (standIn is null)
        {
            steps.Add(new VerifyStep(StandInStep, VerifyOutcome.Failed,
                $"There is no stand-in below this runtime ({install.StandInDir}), so games can't start Monado through Mirador."));
        }
        else
        {
            onLine("Starting a game with no Monado running, as a game would…");
            var wait = services.ExpectStandIn(install);
            await using (wait.ConfigureAwait(false))
            {
                var step = await SessionStepAsync(StandInStep, "d3d11", ShortFrames, install, onLine, ct).ConfigureAwait(false);
                if (step.Outcome == VerifyOutcome.Passed)
                {
                    steps.Add(wait.WasAsked
                        ? step with { Detail = "A game started Monado through the stand-in and ran. " + step.Detail }
                        : step with { Outcome = VerifyOutcome.Failed, Detail = "The game ran, but its stand-in never asked Mirador: was another Monado running?" });
                }
                else
                {
                    steps.Add(wait.Failure is { } why
                        ? step with { Detail = $"The stand-in asked, but Mirador couldn't start Monado for it: {why}" }
                        : wait.WasAsked ? step : step with { Detail = step.Detail + " Its stand-in never asked Mirador." });
                }
            }

            ct.ThrowIfCancellationRequested();
        }

        onLine("Starting Monado as administrator, headless…");
        var elevated = await services.StartElevatedHeadlessAsync(install, ct).ConfigureAwait(false);
        bool? hasFix = null;
        if (elevated.Running is null)
        {
            steps.Add(new VerifyStep(
                ElevatedStep, elevated.Failed ? VerifyOutcome.Failed : VerifyOutcome.Skipped, elevated.WhyNot ?? "Monado can't run as administrator here."));
        }
        else
        {
            await using (elevated.Running.ConfigureAwait(false))
            {
                var step = await SessionStepAsync(ElevatedStep, "d3d11", ShortFrames, install, onLine, ct).ConfigureAwait(false);
                var ranAsYou = steps.Any(s => s.Name == D3D11Step && s.Outcome == VerifyOutcome.Passed);
                if (step.Outcome == VerifyOutcome.Passed && elevated.PipeRelaxed)
                {
                    steps.Add(step with { Detail = "A game running as you connected to Monado running as administrator. Whether this build has the elevation fix can't be told while Windows' Administrator protection is on: Monado then runs with its pipe open to every signed-in user." });
                }
                else if (step.Outcome == VerifyOutcome.Passed)
                {
                    hasFix = true;
                    steps.Add(step with { Detail = "A game running as you connected to Monado running as administrator: this build has the elevation fix." });
                }
                else if (ranAsYou)
                {
                    hasFix = false;
                    steps.Add(step with { Detail = $"A game running as you couldn't use Monado running as administrator, though it ran with Monado as you: this build lacks the elevation fix. {step.Detail}" });
                }
                else
                {
                    steps.Add(step with { Detail = $"Couldn't tell whether this build has the elevation fix: the game failed with Monado as you too. {step.Detail}" });
                }
            }
        }

        foreach (var step in steps)
        {
            onLine($"{step.Name}: {step.Outcome}. {step.Detail}");
        }

        return new VerifyResult(key, time.GetUtcNow(), steps, hasFix, StrictPipe: elevated is { Running: not null, PipeRelaxed: false });
    }

    private async Task<VerifyStep> SessionStepAsync(string name, string api, int frames, MonadoInstall install, Action<string> onLine, CancellationToken ct)
    {
        var output = new List<string>();
        var spec = new CommandSpec(
            xrCheck,
            ["--api", api, "--frames", frames.ToString(System.Globalization.CultureInfo.InvariantCulture), "--timeout", ClientTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            null,
            new Dictionary<string, string?> { ["XR_RUNTIME_JSON"] = install.Manifest });
        int code;
        try
        {
            code = await runner.RunAsync(spec, line =>
            {
                lock (output)
                {
                    output.Add(line);
                }

                if (!line.StartsWith(XrCheckReport.Prefix, StringComparison.Ordinal))
                {
                    onLine(line);
                }
            }, ct).ConfigureAwait(false);
        }
        catch (MiradorException e)
        {
            return new VerifyStep(name, VerifyOutcome.Failed, e.Message);
        }

        XrCheckReport? report;
        lock (output)
        {
            report = XrCheckReport.Parse(output);
        }

        if (report is null)
        {
            return new VerifyStep(name, VerifyOutcome.Failed, $"xr-check gave no result (exit code {code}).");
        }

        return report.Ok && code == 0
            ? new VerifyStep(name, VerifyOutcome.Passed,
                $"{report.Frames} frames{(report.Submitted >= 0 ? $" ({report.Submitted} rendered)" : "")} with {report.System} ({report.Runtime}).")
            : new VerifyStep(name, VerifyOutcome.Failed, $"{report.Error} (after {report.Frames} frames).");
    }

    public static string KeyFor(MonadoInstall install)
    {
        var dir = Path.GetDirectoryName(install.Manifest) ?? "";
        if (BuildInfo.Load(dir) is { Commit: { Length: > 0 } commit } info)
        {
            return info.BuiltAt is { Length: > 0 } builtAt ? $"{commit}@{builtAt}" : commit;
        }

        if (BuildCheckResult.Load(dir)?.Commit is { Length: > 0 } checkedCommit)
        {
            return checkedCommit;
        }

        try
        {
            using var stream = File.OpenRead(install.ServiceExe);
            return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream))[..16];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(VerifyResult))]
[JsonSerializable(typeof(XrCheckReport))]
internal sealed partial class VerifyJsonContext : JsonSerializerContext;
