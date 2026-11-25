using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using Mirador.Core.Modules;
using Mirador.Core.Profiles;

namespace Mirador.Core.Diagnostics;

public sealed record OpenVrFiles(string PathsFile, string LogFile);

public sealed class DiagnosticsZip : IDiagnosticsSink
{
    private readonly List<(string Entry, string? File, string? Text)> entries = [];
    private readonly List<string> refused = [];

    public IReadOnlyList<string> EntryNames => entries.Select(e => e.Entry).ToList();

    public void AddFile(string entryName, string path) => Add(entryName, path, null);

    public void AddText(string entryName, string text) => Add(entryName, null, text);

    public void AddMissing(string entryName, string why) => refused.Add($"{entryName}: {why}");

    public static DiagnosticsZip Collect(
        AppPaths paths, Profile profile, IReadOnlyDictionary<string, string>? lastEnvironment, string versions, IHeadsetModule? module, ModuleContext ctx,
        OpenVrFiles? openVr = null, IDirectMode? directMode = null)
    {
        var zip = new DiagnosticsZip();
        if (NewestMonadoLog(paths.LogsDir) is { } monadoLog)
        {
            zip.AddFile("monado.log", monadoLog);
        }
        else
        {
            zip.AddMissing("monado.log", $"no Monado log yet in {paths.LogsDir}");
        }

        zip.AddFile("mirador.log", paths.AppLogFile);
        if (NewestVerifyLog(paths.LogsDir) is { } verifyLog)
        {
            zip.AddFile("verify.log", verifyLog);
        }

        zip.AddText("profile.json", JsonFile.Text(profile, DiagnosticsJsonContext.Default.Profile));
        zip.AddFile("monado-settings.json", paths.MonadoSettingsFile);
        zip.AddText(
            "environment.txt",
            lastEnvironment is null
                ? "Monado hasn't been started since Mirador started.\n"
                : string.Concat(lastEnvironment.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}={kv.Value}\n")));
        zip.AddText("versions.txt", versions);
        if (openVr is not null)
        {
            zip.AddFile("openvrpaths.vrpath", openVr.PathsFile);
            var logName = string.Equals(Path.GetFileName(openVr.LogFile), "opencomposite.log", StringComparison.OrdinalIgnoreCase)
                ? "opencomposite.log"
                : "openlamina.log";
            zip.AddFile(logName, openVr.LogFile);
        }

        directMode?.AddDiagnostics(ctx, zip);
        module?.AddDiagnostics(ctx, profile, zip);
        return zip;
    }

    public void Save(string zipPath)
    {
        var missing = new StringBuilder();
        foreach (var line in refused)
        {
            missing.Append(line).Append('\n');
        }

        var temp = zipPath + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var (entry, file, text) in entries)
                {
                    if (text is not null)
                    {
                        using var writer = new StreamWriter(archive.CreateEntry(entry).Open(), new UTF8Encoding(false));
                        writer.Write(text);
                        continue;
                    }

                    FileStream source;
                    try
                    {
                        source = new FileStream(file!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                    {
                        missing.Append(entry).Append(": ").Append(file).Append(" isn't there\n");
                        continue;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        missing.Append(entry).Append(": ").Append(file).Append(" couldn't be read (").Append(e.Message.Trim()).Append(")\n");
                        continue;
                    }

                    using (source)
                    using (var target = archive.CreateEntry(entry).Open())
                    {
                        source.CopyTo(target);
                    }
                }

                if (missing.Length > 0)
                {
                    using var writer = new StreamWriter(archive.CreateEntry("missing.txt").Open(), new UTF8Encoding(false));
                    writer.Write(missing.ToString());
                }
            }

            File.Move(temp, zipPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    private void Add(string entryName, string? file, string? text)
    {
        var name = entryName.Replace('\\', '/');
        var plain = name.Length > 0
            && !Path.IsPathRooted(name)
            && !name.Contains(':')
            && name.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
        if (!plain || name == "missing.txt" || entries.Any(e => string.Equals(e.Entry, name, StringComparison.OrdinalIgnoreCase)))
        {
            refused.Add($"{entryName}: left out (not a plain relative name, or taken already)");
            return;
        }

        entries.Add((name, file, text));
    }

    public static string? NewestMonadoLog(string logsDir) =>
        Directory.Exists(logsDir)
            ? Directory.EnumerateFiles(logsDir, "monado-*.log").OrderBy(Path.GetFileName, StringComparer.Ordinal).LastOrDefault()
            : null;

    public const string VerifyLogPrefix = "verify-run-";

    public static string? NewestVerifyLog(string logsDir) =>
        Directory.Exists(logsDir)
            ? Directory.EnumerateFiles(logsDir, VerifyLogPrefix + "*.log").OrderBy(Path.GetFileName, StringComparer.Ordinal).LastOrDefault()
            : null;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(Profile))]
internal sealed partial class DiagnosticsJsonContext : JsonSerializerContext;
