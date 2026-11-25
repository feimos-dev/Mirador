using System.Text.Json.Serialization;
using Mirador.Core.Monado;

namespace Mirador.Core.Build;

public enum CheckOutcome
{
    Passed,
    Failed,

    Skipped,
}

public sealed record BuildCheck(string Name, CheckOutcome Outcome, string Detail);

public sealed record BuildCheckResult(int Format, string? Commit, DateTimeOffset CheckedAt, IReadOnlyList<BuildCheck> Checks)
{
    public const string FileName = "checks.json";

    [JsonIgnore]
    public bool AllPassed => Checks.All(c => c.Outcome != CheckOutcome.Failed);

    public string Summary()
    {
        var failed = Checks.Where(c => c.Outcome == CheckOutcome.Failed).Select(c => c.Name).ToList();
        return failed.Count == 0
            ? "All checks passed."
            : $"Failed: {string.Join(", ", failed)}. The build may still work; see the build log.";
    }

    public void Save(string installDir) =>
        JsonFile.Save(Path.Combine(installDir, FileName), this, BuildJsonContext.Default.BuildCheckResult);

    public static BuildCheckResult? Load(string installDir)
    {
        var path = Path.Combine(installDir, FileName);
        try
        {
            return File.Exists(path)
                ? System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(path), BuildJsonContext.Default.BuildCheckResult)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

public sealed class BuildChecks(ICommandRunner runner, string systemDir, TimeProvider time)
{
    public const string ManifestFile = "openxr_monado.json";

    public static readonly string RuntimeDllFile = Path.Combine("bin", "openxr_monado.dll");

    public static readonly string ServiceFile = Path.Combine("bin", "monado-service.exe");

    public static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    public async Task<BuildCheckResult> RunAsync(
        string installDir, string? buildDir, string? ctest, string? commit, Action<string> log, CancellationToken ct)
    {
        var checks = new List<BuildCheck>
        {
            Files(installDir),
            Manifest(installDir),
            Dlls(installDir),
            ServiceSetting(buildDir),
        };
        checks.Add(await UnitTestsAsync(buildDir, ctest, log, ct).ConfigureAwait(false));
        foreach (var check in checks)
        {
            log($"Check: {check.Name}: {check.Outcome}. {check.Detail}");
        }

        return new BuildCheckResult(1, commit, time.GetUtcNow(), checks);
    }

    private static BuildCheck Files(string installDir)
    {
        const string name = "Files";
        var missing = new[] { ManifestFile, RuntimeDllFile, ServiceFile }
            .Where(f => !File.Exists(Path.Combine(installDir, f)))
            .ToList();
        return missing.Count == 0
            ? new BuildCheck(name, CheckOutcome.Passed, "The runtime manifest, its DLL and monado-service.exe are there.")
            : new BuildCheck(name, CheckOutcome.Failed, $"Missing: {string.Join(", ", missing)}.");
    }

    private static BuildCheck Manifest(string installDir)
    {
        const string name = "Runtime manifest";
        var manifest = Path.Combine(installDir, ManifestFile);
        var problem = RuntimeManifest.Validate(manifest);
        if (problem is not null)
        {
            return new BuildCheck(name, CheckOutcome.Failed, problem);
        }

        var library = RuntimeManifest.ReadLibraryPath(manifest);
        return string.Equals(library, Path.GetFullPath(Path.Combine(installDir, RuntimeDllFile)), StringComparison.OrdinalIgnoreCase)
            ? new BuildCheck(name, CheckOutcome.Passed, $"It names {RuntimeDllFile}.")
            : new BuildCheck(name, CheckOutcome.Failed, $"It names {library}, not the install's {RuntimeDllFile}.");
    }

    private BuildCheck Dlls(string installDir)
    {
        const string name = "DLLs";
        var bin = Path.Combine(installDir, "bin");
        var start = new[] { RuntimeDllFile, ServiceFile }.Select(f => Path.Combine(installDir, f)).Where(File.Exists).ToList();
        if (start.Count == 0)
        {
            return new BuildCheck(name, CheckOutcome.Skipped, "Nothing to check: the runtime DLL and the service are missing.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var broken = new List<string>();
        var queue = new Queue<string>(start);
        while (queue.TryDequeue(out var file))
        {
            IReadOnlyList<string> imports;
            try
            {
                imports = PeImports.Read(file);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                broken.Add($"{Path.GetFileName(file)} ({e.Message.TrimEnd('.')})");
                continue;
            }

            foreach (var dll in imports)
            {
                if (!seen.Add(dll) || IsApiSet(dll))
                {
                    continue;
                }

                var local = Path.Combine(bin, dll);
                if (File.Exists(local))
                {
                    queue.Enqueue(local);
                }
                else if (!File.Exists(Path.Combine(systemDir, dll)))
                {
                    missing.Add($"{dll} (needed by {Path.GetFileName(file)})");
                }
            }
        }

        if (missing.Count == 0 && broken.Count == 0)
        {
            return new BuildCheck(name, CheckOutcome.Passed, $"All {seen.Count} DLLs needed are in bin or Windows.");
        }

        var parts = new List<string>();
        if (missing.Count > 0)
        {
            parts.Add($"Not found: {string.Join(", ", missing)}.");
        }

        if (broken.Count > 0)
        {
            parts.Add($"Unreadable: {string.Join(", ", broken)}.");
        }

        return new BuildCheck(name, CheckOutcome.Failed, string.Join(" ", parts));
    }

    private static bool IsApiSet(string dll) =>
        dll.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase) || dll.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase);

    private static BuildCheck ServiceSetting(string? buildDir)
    {
        const string name = "Service setting";
        if (buildDir is null)
        {
            return new BuildCheck(name, CheckOutcome.Skipped, "No build folder here (a downloaded build: CI checked it).");
        }

        var cache = Path.Combine(buildDir, "CMakeCache.txt");
        if (!File.Exists(cache))
        {
            return new BuildCheck(name, CheckOutcome.Failed, $"{cache} is missing.");
        }

        var value = CMakeCache.Read(cache, "XRT_SERVICE_EXECUTABLE");
        return Monado.ServiceLaunch.StartsStandIn(value)
            ? new BuildCheck(name, CheckOutcome.Passed, $"XRT_SERVICE_EXECUTABLE is {value}: games can start Monado through Mirador.")
            : new BuildCheck(name, CheckOutcome.Failed,
                $"XRT_SERVICE_EXECUTABLE is {(value is null ? "not set" : $"'{value}'")}: games can't start this Monado by themselves.");
    }

    private async Task<BuildCheck> UnitTestsAsync(string? buildDir, string? ctest, Action<string> log, CancellationToken ct)
    {
        const string name = "Unit tests";
        if (buildDir is null || ctest is null)
        {
            return new BuildCheck(name, CheckOutcome.Skipped,
                buildDir is null ? "No build folder here (a downloaded build: CI ran them)." : "ctest wasn't found.");
        }

        var failures = new List<string>();
        var noTests = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TestTimeout);
        int code;
        try
        {
            code = await runner.RunAsync(
                new CommandSpec(ctest, ["--test-dir", buildDir, "--output-on-failure", "--timeout", "120"]),
                line =>
                {
                    log(line);

                    noTests |= line.Contains("No tests were found", StringComparison.Ordinal);

                    if (line.TrimStart().Length > 0 && char.IsDigit(line.TrimStart()[0]) && line.Contains(" - ", StringComparison.Ordinal)
                        && (line.EndsWith("(Failed)", StringComparison.Ordinal) || line.EndsWith("(Timeout)", StringComparison.Ordinal)
                            || line.Contains("(SEGFAULT)", StringComparison.Ordinal) || line.Contains("(Exception)", StringComparison.Ordinal)))
                    {
                        failures.Add(line.Split(" - ", 2)[1].Trim());
                    }
                },
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new BuildCheck(name, CheckOutcome.Failed, $"They took longer than {TestTimeout.TotalMinutes:0} minutes.");
        }
        catch (MiradorException e)
        {
            return new BuildCheck(name, CheckOutcome.Failed, e.Message);
        }

        if (code == 0 && noTests)
        {
            return new BuildCheck(name, CheckOutcome.Skipped, "This build has no unit tests (ctest found none).");
        }

        return code == 0
            ? new BuildCheck(name, CheckOutcome.Passed, "ctest passed.")
            : new BuildCheck(name, CheckOutcome.Failed,
                failures.Count > 0 ? $"Failed: {string.Join(", ", failures)}." : $"ctest failed (exit code {code}).");
    }
}

public static class CMakeCache
{
    public static string? Read(string cacheFile, string key)
    {
        foreach (var line in File.ReadLines(cacheFile))
        {
            if (!line.StartsWith(key + ":", StringComparison.Ordinal))
            {
                continue;
            }

            var equals = line.IndexOf('=', key.Length);
            if (equals > 0)
            {
                return line[(equals + 1)..];
            }
        }

        return null;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(BuildCheckResult))]
internal sealed partial class BuildJsonContext : JsonSerializerContext;
