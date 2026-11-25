using System.Net;
using System.Security.Cryptography;
using Mirador.Core.Build;
using Mirador.Core.Diagnostics;
using Mirador.Core.OpenVr;
using Mirador.Core.Tests.Build;

namespace Mirador.Core.Tests.OpenVr;

public sealed class OpenCompositeInstallTests : IDisposable
{
    private const string Url = "https://github.com/feimos-dev/OpenLamina/releases/download/v2026.11.20/OpenLamina-Windows-Installer.exe";
    private const string Source = "OpenLamina 2026.11.20, commit 75342495640501953fc2c17f73eeb4d124e8528e\n";
    private static readonly byte[] Setup = [0x4D, 0x5A, 1, 2, 3, 4];
    private static readonly string SetupSha = Convert.ToHexStringLower(SHA256.HashData(Setup));

    private readonly TempDir dir = new();
    private readonly FakeCommandRunner runner = new();
    private readonly Answer answer = new();

    // The release key, made for the test
    private readonly ECDsa key;
    private readonly string publicKey;

    public OpenCompositeInstallTests()
    {
        (key, publicKey) = SignedSumsTests.NewKey();
        answer.Files["OpenLamina-Windows-Installer.exe"] = Setup;

        // The installer puts the release in its folder, as OpenLamina-Windows-Installer.exe /VERYSILENT does.
        runner.Effect = (spec, _) =>
        {
            Directory.CreateDirectory(Path.Combine(Folder, "bin"));
            File.WriteAllBytes(Path.Combine(Folder, "bin", "vrclient_x64.dll"), [1]);
            File.WriteAllText(Path.Combine(Folder, "SOURCE.txt"), InstalledSource);
            return Task.CompletedTask;
        };
    }

    public void Dispose()
    {
        key.Dispose();
        dir.Dispose();
    }

    private string Folder => dir.File("Programs", "OpenLamina");

    private string Downloads => dir.File("Mirador", "tools", "download");

    private string InstalledSource { get; set; } = Source;

    private OpenCompositeInstall Install => new(Folder, runner, publicKey);

    /// <summary>
    /// A release whose installer GitHub lists with <paramref name="sha"/> (default: the installer's), and whose
    /// SHA256SUMS.txt lists <paramref name="listed"/> (default: the same), signed for <paramref name="signedFor"/>
    /// (default: its tag) by <paramref name="signer"/> (default: the release key).
    /// </summary>
    private Release Rel(string? url = Url, string? sha = null, string tag = "v2026.11.20", string? listed = null, string? signedFor = null, ECDsa? signer = null)
    {
        var digest = sha ?? SetupSha;
        var sums = System.Text.Encoding.UTF8.GetBytes(
            $"{new string('1', 64)}  OpenLamina-Windows-Portable.zip\n{listed ?? digest}  OpenLamina-Windows-Installer.exe\n");
        answer.Files[SignedSums.SumsName] = sums;
        answer.Files[SignedSums.SignatureName] = SignedSumsTests.Sign(signer ?? key, SignedSums.SignedMessage(signedFor ?? tag, sums));
        var download = $"https://github.com/feimos-dev/OpenLamina/releases/download/{tag}/";
        return new(
            tag, "https://github.com/feimos-dev/OpenLamina/releases/tag/" + tag, url, digest,
            download + SignedSums.SumsName, download + SignedSums.SignatureName);
    }

    private bool InstallerRequested => answer.Requested.Any(u => u.AbsolutePath.EndsWith("/OpenLamina-Windows-Installer.exe", StringComparison.Ordinal));

    private Task<InstallOutcome> RunAsync(Release release) =>
        Install.InstallAsync(new HttpClient(answer), release, Downloads, CancellationToken.None);

    [Fact]
    public async Task The_release_installer_runs_silently_and_the_release_is_installed()
    {
        Assert.False(Install.IsInstalled);
        Assert.Equal("OpenLamina: not installed", Install.VersionLine());

        Assert.Equal(InstallOutcome.Installed, await RunAsync(Rel()));

        // The signed checksums first, then the installer they list.
        Assert.Equal(
            ["SHA256SUMS.txt", "SHA256SUMS.txt.sig", "OpenLamina-Windows-Installer.exe"],
            answer.Requested.Select(u => u.Segments[^1]));
        var run = Assert.Single(runner.Commands);
        Assert.Equal(Path.Combine(Downloads, "OpenLamina-Windows-Installer-2026.11.20.exe"), run.FileName);
        Assert.Equal(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCLOSEAPPLICATIONS"], run.Arguments);
        Assert.True(Install.IsInstalled);
        Assert.Equal("2026.11.20", Install.Source?.Version);
        Assert.Equal("OpenLamina: 2026.11.20 (7534249)", Install.VersionLine());
        Assert.Empty(Directory.GetFiles(Downloads)); // the installer doesn't stay
    }

    [Fact]
    public async Task An_installer_that_isnt_the_one_the_signed_checksums_list_is_never_run()
    {
        var e = await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel(sha: new string('0', 64))));

        Assert.Equal("OpenLamina's installer's download doesn't match the SHA-256 its signed checksums list, so it was not used.", e.Message);
        Assert.Empty(runner.Commands);
        Assert.False(Install.IsInstalled);
        Assert.Empty(Directory.GetFiles(Downloads));
    }

    [Fact]
    public async Task A_release_without_signed_checksums_is_refused()
    {
        var e = await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel() with { SumsUrl = null, SignatureUrl = null }));
        Assert.Equal("OpenLamina 2026.11.20 was refused: the release has no signed checksums.", e.Message);

        await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel() with { SignatureUrl = null }));
        await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel() with { SumsUrl = null }));

        Assert.Empty(answer.Requested);
        Assert.Empty(runner.Commands);
    }

    [Theory]
    [InlineData("http://github.com/feimos-dev/OpenLamina/releases/download/v2026.11.20/SHA256SUMS.txt")]
    [InlineData("https://example.com/SHA256SUMS.txt")]
    public async Task Checksums_not_on_github_are_never_fetched(string url)
    {
        await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel() with { SumsUrl = url }));
        await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel() with { SignatureUrl = url + ".sig" }));

        Assert.Empty(answer.Requested);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Checksums_signed_with_another_key_are_refused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var e = await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel(signer: other)));

        Assert.Equal("OpenLamina 2026.11.20 was refused: its SHA256SUMS.txt isn't signed for v2026.11.20 with OpenLamina's release key.", e.Message);
        Assert.False(InstallerRequested);
        Assert.Empty(runner.Commands);
        Assert.Empty(Directory.GetFiles(Downloads));
    }

    [Fact]
    public async Task Another_releases_signed_checksums_are_refused()
    {
        var e = await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel(signedFor: "v2026.11.06")));

        Assert.Contains("isn't signed for v2026.11.20", e.Message);
        Assert.False(InstallerRequested);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Changed_checksums_are_refused()
    {
        var release = Rel();
        var sums = answer.Files[SignedSums.SumsName];
        sums[0] = (byte)'2'; // the portable zip's line: the installer's still matches

        await Assert.ThrowsAsync<MiradorException>(() => RunAsync(release));

        Assert.False(InstallerRequested);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Checksums_that_dont_list_the_installer_are_refused()
    {
        var release = Rel();
        var sums = System.Text.Encoding.UTF8.GetBytes($"{SetupSha}  OpenLamina-Windows-Portable.zip\n");
        answer.Files[SignedSums.SumsName] = sums;
        answer.Files[SignedSums.SignatureName] = SignedSumsTests.Sign(key, SignedSums.SignedMessage(release.Tag, sums));

        var e = await Assert.ThrowsAsync<MiradorException>(() => RunAsync(release));

        Assert.Equal("OpenLamina 2026.11.20 was refused: its signed SHA256SUMS.txt doesn't list OpenLamina-Windows-Installer.exe.", e.Message);
        Assert.False(InstallerRequested);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task A_github_digest_other_than_the_signed_one_is_refused()
    {
        var e = await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel(listed: new string('0', 64))));

        Assert.Equal(
            "OpenLamina 2026.11.20 was refused: GitHub lists another SHA-256 for OpenLamina-Windows-Installer.exe than its signed SHA256SUMS.txt.",
            e.Message);
        Assert.False(InstallerRequested);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task A_missing_or_oversized_checksums_file_says_so()
    {
        var release = Rel();
        answer.Files.Remove(SignedSums.SignatureName);

        var e = await Assert.ThrowsAsync<MiradorException>(() => RunAsync(release));
        Assert.Equal("SHA256SUMS.txt.sig's download failed (404 Not Found).", e.Message);

        answer.Files[SignedSums.SignatureName] = new byte[SignedSums.MaxBytes + 1];
        e = await Assert.ThrowsAsync<MiradorException>(() => RunAsync(release));
        Assert.Contains("larger than Mirador allows", e.Message);

        Assert.False(InstallerRequested);
        Assert.Empty(runner.Commands);
        Assert.Empty(Directory.GetFiles(Downloads));
    }

    [Theory]
    [InlineData("http://github.com/feimos-dev/OpenLamina/releases/download/v2026.11.20/OpenLamina-Windows-Installer.exe")]
    [InlineData("https://example.com/OpenLamina-Windows-Installer.exe")]
    [InlineData(null)]
    public async Task An_installer_not_on_github_is_never_fetched(string? url)
    {
        await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel(url)));

        Assert.Empty(answer.Requested);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task A_game_holding_openlamina_leaves_it_as_it_is()
    {
        await RunAsync(Rel());
        runner.Commands.Clear();
        answer.Requested.Clear();

        using (new FileStream(Path.Combine(Folder, "bin", "vrclient_x64.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(Install.InUse);
            Assert.Equal(InstallOutcome.InUse, await RunAsync(Rel(tag: "v2026.11.21")));
        }

        Assert.Empty(answer.Requested);
        Assert.Empty(runner.Commands);
        Assert.False(Install.InUse);

        // A 32-bit game holds the DLL beside it.
        File.WriteAllBytes(Path.Combine(Folder, "bin", "vrclient.dll"), [2]);
        using (new FileStream(Path.Combine(Folder, "bin", "vrclient.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(Install.InUse);
        }
    }

    [Fact]
    public async Task One_install_at_a_time()
    {
        var running = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        runner.Effect = async (_, _) =>
        {
            running.SetResult();
            await release.Task;
        };
        var install = Install;
        var first = install.InstallAsync(new HttpClient(answer), Rel(), Downloads, CancellationToken.None);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var e = await Assert.ThrowsAsync<MiradorException>(() => install.InstallAsync(new HttpClient(answer), Rel(), Downloads, CancellationToken.None));

        Assert.Equal("OpenLamina is being installed already: wait for that to finish.", e.Message);
        release.SetResult();
        await Assert.ThrowsAsync<MiradorException>(() => first.WaitAsync(TimeSpan.FromSeconds(10))); // its fake put nothing in place
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task A_failed_installer_says_so()
    {
        runner.Effect = null;
        runner.Answer = _ => ([], 5);

        var e = await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel()));

        Assert.Contains("ended with code 5", e.Message);
    }

    [Fact]
    public async Task An_installer_that_put_another_release_in_place_says_so()
    {
        InstalledSource = "OpenLamina 2026.11.06, commit 75342495640501953fc2c17f73eeb4d124e8528e\n";

        var e = await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel()));

        Assert.Contains("doesn't hold OpenLamina 2026.11.20", e.Message);
    }

    [Fact]
    public async Task A_release_without_an_installer_is_refused()
    {
        var e = await Assert.ThrowsAsync<MiradorException>(() => RunAsync(new Release("v2026.11.06", "https://github.com", null, null)));

        Assert.Equal("OpenLamina 2026.11.06 has no installer that Mirador can check.", e.Message);
    }

    /// <summary>GitHub's release files, by name; any other is not found.</summary>
    private sealed class Answer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(Files.TryGetValue(request.RequestUri!.Segments[^1], out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
