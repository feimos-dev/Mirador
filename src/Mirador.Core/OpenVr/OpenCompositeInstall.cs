using System.Text.RegularExpressions;
using Mirador.Core.Build;
using Mirador.Core.Diagnostics;
using Mirador.Ipc;

namespace Mirador.Core.OpenVr;

public enum InstallOutcome
{
    Installed,

    InUse,
}

public sealed partial class OpenCompositeInstall(string folder, ICommandRunner? runner = null, string releaseKey = SignedSums.PublicKey)
{
    private readonly ICommandRunner runner = runner ?? new CommandRunner();

    public const long MaxSetupBytes = 64 * 1024 * 1024;

    public static readonly TimeSpan SetupLimit = TimeSpan.FromMinutes(5);

    private const string Dll = "vrclient_x64.dll";

    private static readonly string[] Dlls = [Dll, "vrclient.dll"];
    private const string Host = "github.com";

    private int installing;

    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenLamina");

    public static string LegacyFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenComposite");

    public static string NewLogFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenLamina", "logs", "openlamina.log");

    public static string LegacyLogFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenComposite", "logs", "opencomposite.log");

    public static string LogFile => !File.Exists(NewLogFile) && File.Exists(LegacyLogFile) ? LegacyLogFile : NewLogFile;

    public string Folder => folder;

    public bool IsInstalled => File.Exists(DllPath);

    public OpenCompositeSource? Source
    {
        get
        {
            try
            {
                var file = Path.Combine(folder, "SOURCE.txt");
                return File.Exists(file) ? OpenCompositeSource.Read(File.ReadAllText(file)) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public bool InUse => Dlls.Any(name => Held(Path.Combine(folder, "bin", name)));

    private static bool Held(string dll)
    {
        if (!File.Exists(dll))
        {
            return false;
        }

        try
        {
            using var held = new FileStream(dll, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string VersionLine() =>
        Source is { } source ? $"OpenLamina: {source.Describe()}" : IsInstalled ? "OpenLamina: installed, version unknown" : "OpenLamina: not installed";

    private string DllPath => Path.Combine(folder, "bin", Dll);

    public async Task<InstallOutcome> InstallAsync(HttpClient http, Release release, string downloadDir, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref installing, 1) == 1)
        {
            throw new MiradorException("OpenLamina is being installed already: wait for that to finish.");
        }

        try
        {
            return await InstallCoreAsync(http, release, downloadDir, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref installing, 0);
        }
    }

    private async Task<InstallOutcome> InstallCoreAsync(HttpClient http, Release release, string downloadDir, CancellationToken ct)
    {
        if (release.InstallerUrl is null || release.InstallerSha256 is null)
        {
            throw new MiradorException($"OpenLamina {Plain(release.Tag)} has no installer that Mirador can check.");
        }

        if (OnGitHub(release.InstallerUrl) is not { } url)
        {
            throw new MiradorException("OpenLamina's installer isn't on GitHub, so Mirador won't fetch it.");
        }

        var version = Plain(release.Tag);
        if (!SafeName().IsMatch(version))
        {
            throw new MiradorException($"OpenLamina's release name \"{release.Tag}\" isn't a version Mirador understands.");
        }

        if (OnGitHub(release.SumsUrl) is not { } sumsUrl || OnGitHub(release.SignatureUrl) is not { } signatureUrl)
        {
            throw new MiradorException($"OpenLamina {version} was refused: the release has no signed checksums.");
        }

        if (InUse)
        {
            return InstallOutcome.InUse;
        }

        Directory.CreateDirectory(downloadDir);
        var setup = Path.Combine(downloadDir, $"OpenLamina-Windows-Installer-{version}.exe");
        var part = setup + ".tmp";
        try
        {
            var sha256 = await SignedSha256Async(http, release, sumsUrl, signatureUrl, downloadDir, ct).ConfigureAwait(false);
            await CheckedDownload.ToFileAsync(
                    http, url, part, MaxSetupBytes, sha256, "OpenLamina's installer", m => new MiradorException(m), ct, hashFrom: "its signed checksums list")
                .ConfigureAwait(false);
            File.Move(part, setup, overwrite: true);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(SetupLimit);
            int code;
            try
            {
                code = await runner.RunAsync(
                    new CommandSpec(setup, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCLOSEAPPLICATIONS"], downloadDir),
                    _ => { },
                    limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new MiradorException($"OpenLamina's installer didn't finish within {SetupLimit.TotalMinutes:0} minutes.");
            }

            if (code != 0)
            {
                throw new MiradorException(InUse
                    ? "A game started using OpenLamina meanwhile, so it wasn't updated. Close the game and try again."
                    : $"OpenLamina's installer ended with code {code}, so OpenLamina may not have changed. If OpenLamina's own window is open, close it and try again.");
            }

            if (Source?.Version != version)
            {
                throw new MiradorException($"OpenLamina's installer ran, but {Folder} doesn't hold OpenLamina {version}.");
            }

            return InstallOutcome.Installed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new MiradorException($"Mirador couldn't run OpenLamina's installer ({e.Message.Trim().TrimEnd('.')}).");
        }
        finally
        {
            Cleanup.TryDelete(part);
            Cleanup.TryDelete(setup);
        }
    }

    private async Task<string> SignedSha256Async(HttpClient http, Release release, Uri sumsUrl, Uri signatureUrl, string downloadDir, CancellationToken ct)
    {
        var sums = await SmallFileAsync(http, sumsUrl, Path.Combine(downloadDir, SignedSums.SumsName + ".tmp"), SignedSums.SumsName, ct).ConfigureAwait(false);
        var signature = await SmallFileAsync(http, signatureUrl, Path.Combine(downloadDir, SignedSums.SignatureName + ".tmp"), SignedSums.SignatureName, ct)
            .ConfigureAwait(false);
        try
        {
            return SignedSums.Expected(release.Tag, sums, signature, UpdateCheck.Installer, release.InstallerSha256, releaseKey);
        }
        catch (InvalidDataException e)
        {
            throw new MiradorException(e.Message);
        }
    }

    private static async Task<byte[]> SmallFileAsync(HttpClient http, Uri url, string file, string what, CancellationToken ct)
    {
        try
        {
            await CheckedDownload.ToFileAsync(http, url, file, SignedSums.MaxBytes, null, what, m => new MiradorException(m), ct).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
        }
        finally
        {
            Cleanup.TryDelete(file);
        }
    }

    private static Uri? OnGitHub(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var url)
        && url.Scheme == Uri.UriSchemeHttps
        && string.Equals(url.Host, Host, StringComparison.OrdinalIgnoreCase)
            ? url
            : null;

    private static string Plain(string tag) => tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z._-]*$")]
    private static partial Regex SafeName();
}
