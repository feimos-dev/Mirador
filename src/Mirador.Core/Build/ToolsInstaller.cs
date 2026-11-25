using System.Text.Json;

using Mirador.Ipc;

namespace Mirador.Core.Build;

public sealed class ToolsInstaller(HttpClient http, ICommandRunner runner, string toolsDir, string vcpkgCommit, Action<string, string>? moveDirectory = null)
{
    public const int MoveAttempts = 5;

    private readonly Action<string, string> move = moveDirectory ?? Directory.Move;

    public const long MaxDownloadBytes = 256L * 1024 * 1024;

    public const long MaxUnpackedBytes = 1024L * 1024 * 1024;

    private static MiradorException Fail(string message) => new(message);

    public const string VcpkgUrl = "https://github.com/microsoft/vcpkg.git";

    public async Task InstallAsync(ToolPin pin, Action<string> onLine, CancellationToken ct)
    {
        var url = Downloads.GitHubUrl(pin.Url, pin.Name);
        Directory.CreateDirectory(toolsDir);
        var final = Path.Combine(toolsDir, pin.Folder);
        var zipFile = final + ".zip.part";
        var staging = final + ".new";
        var old = final + ".old";
        try
        {
            onLine($"Downloading {pin.Name} {pin.Version} from GitHub…");
            await CheckedDownload.ToFileAsync(http, url, zipFile, MaxDownloadBytes, pin.Sha256, pin.Name, Fail, ct, hashFrom: "Mirador pins for it").ConfigureAwait(false);

            onLine($"Unpacking {pin.Name}…");
            DeleteFolder(staging);
            CheckedDownload.Unpack(zipFile, staging, MaxUnpackedBytes, pin.Name, Fail, pin.Exe);
            if (!File.Exists(Path.Combine(staging, pin.Exe)))
            {
                throw new MiradorException($"The {pin.Name} download isn't the one Mirador expects: it has no {pin.Exe}.");
            }

            await File.WriteAllTextAsync(
                Path.Combine(staging, BuildToolsCheck.MarkerFile),
                JsonSerializer.Serialize(new BuildToolsCheck.Marker(pin.Version, pin.Sha256.ToLowerInvariant())),
                CancellationToken.None).ConfigureAwait(false);

            DeleteFolder(old);
            var hadOld = Directory.Exists(final);
            if (hadOld && !TryMove(final, old))
            {
                throw new MiradorException($"Mirador's {pin.Name} is in use (a build may be running): try again when it is done.");
            }

            if (!TryMove(staging, final))
            {
                if (hadOld && !TryMove(old, final))
                {
                    throw new MiradorException($"Mirador couldn't put {pin.Name} in place, nor its old copy back: it is in {old}.");
                }

                throw new MiradorException($"Mirador couldn't put {pin.Name} in place (another program may be holding it): try again.");
            }

            onLine($"{pin.Name} {pin.Version} is ready.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new MiradorException($"Mirador couldn't unpack {pin.Name} ({e.Message.Trim().TrimEnd('.')}).");
        }
        finally
        {
            Cleanup.TryDelete(zipFile);
            Cleanup.TryDeleteDirectory(staging);
            if (Directory.Exists(final))
            {
                Cleanup.TryDeleteDirectory(old);
            }
        }
    }

    private bool TryMove(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                move(from, to);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == MoveAttempts)
                {
                    return false;
                }

                Thread.Sleep(200);
            }
        }
    }

    public async Task InstallVcpkgAsync(string git, Action<string> onLine, CancellationToken ct)
    {
        if (!BuildToolsCheck.Commit().IsMatch(vcpkgCommit))
        {
            throw new MiradorException("Mirador's vcpkg pin (tools\\vcpkg-commit.txt) isn't a commit, so vcpkg wasn't fetched.");
        }

        Directory.CreateDirectory(toolsDir);
        var final = Path.Combine(toolsDir, "vcpkg");
        var existing = Directory.Exists(Path.Combine(final, ".git"));
        var work = existing ? final : final + ".new";
        var lastLine = "";
        void Line(string line)
        {
            lastLine = line;
            onLine(line);
        }

        var environment = new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never" };
        async Task Git(string step, string? where, params string[] args)
        {
            var code = await runner.RunAsync(
                new CommandSpec(git, ["-c", "credential.helper=", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always", .. args], where, environment),
                Line, ct).ConfigureAwait(false);
            if (code != 0)
            {
                var why = lastLine.Trim();
                throw new MiradorException($"Mirador couldn't {step} vcpkg (git exit code {code}{(why.Length > 0 ? ": " + why : "")}).");
            }
        }

        try
        {
            if (existing)
            {
                onLine("Fetching vcpkg from GitHub…");
                await Git("fetch", work, "fetch", "--quiet", VcpkgUrl).ConfigureAwait(false);
            }
            else
            {
                DeleteFolder(work);
                onLine("Cloning vcpkg from GitHub (all of it: its manifests need its history)…");
                await Git("clone", toolsDir, "clone", "--quiet", "--no-checkout", VcpkgUrl, work).ConfigureAwait(false);
            }

            await Git("check out", work, "-c", "advice.detachedHead=false", "checkout", "--quiet", "--force", "--detach", vcpkgCommit).ConfigureAwait(false);

            var head = new List<string>();
            var code = await runner.RunAsync(new CommandSpec(git, ["rev-parse", "HEAD"], work, environment), head.Add, ct).ConfigureAwait(false);
            if (code != 0 || head.FirstOrDefault()?.Trim() is not { } reached || !string.Equals(reached, vcpkgCommit, StringComparison.OrdinalIgnoreCase))
            {
                throw new MiradorException($"vcpkg didn't end at Mirador's pinned commit {vcpkgCommit[..7]}, so it isn't used.");
            }

            if (existing)
            {
                File.Delete(Path.Combine(work, "vcpkg.exe"));
            }

            if (!existing && !TryMove(work, final))
            {
                throw new MiradorException("Mirador couldn't put vcpkg in place (another program may be holding it): try again.");
            }

            onLine($"vcpkg is ready at commit {vcpkgCommit[..7]}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new MiradorException($"Mirador couldn't put vcpkg in place ({e.Message.Trim().TrimEnd('.')}).");
        }
        finally
        {
            if (!existing)
            {
                Cleanup.TryDeleteDirectory(final + ".new");
            }
        }
    }

    private static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

}

public static class VsBootstrapper
{
    public const string Url = "https://aka.ms/vs/17/release/vs_buildtools.exe";

    public const long MaxBytes = 64L * 1024 * 1024;

    public static async Task<BootstrapperFile> DownloadAsync(HttpClient http, string downloadRoot, Func<string, string?> signerOf, CancellationToken ct)
    {
        var folder = Path.Combine(downloadRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "vs_buildtools.exe");
        FileStream? held = null;
        try
        {
            await CheckedDownload.ToFileAsync(http, new Uri(Url), file, MaxBytes, null, "the Visual Studio Build Tools installer", m => new MiradorException(m), ct).ConfigureAwait(false);
            held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Mirador.Ipc.SignerName.Is(signerOf(file), "Microsoft Corporation"))
            {
                throw new MiradorException("The Visual Studio Build Tools installer Mirador downloaded isn't signed by Microsoft, so it was not run.");
            }

            return new BootstrapperFile(file, held);
        }
        catch
        {
            held?.Dispose();
            Cleanup.TryDeleteDirectory(folder);
            throw;
        }
    }
}

public sealed class BootstrapperFile(string path, FileStream held) : IDisposable
{
    public string Path { get; } = path;

    public void Dispose()
    {
        held.Dispose();
        Cleanup.TryDeleteDirectory(System.IO.Path.GetDirectoryName(Path)!);
    }

}

internal static class Downloads
{
    public static Uri GitHubUrl(string url, string name) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            ? uri
            : throw new MiradorException($"{name}'s download isn't on GitHub, so Mirador won't fetch it.");

}
