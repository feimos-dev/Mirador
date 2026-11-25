using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Mirador.Core.OpenVr;

namespace Mirador.Core.Tests.OpenVr;

public class SignedSumsTests
{
    private static readonly byte[] Sums = Encoding.UTF8.GetBytes(
        "1111111111111111111111111111111111111111111111111111111111111111  OpenLamina-Windows-Portable.zip\n" +
        "2222222222222222222222222222222222222222222222222222222222222222  OpenLamina-Windows-Installer.exe\n");

    private const string Tag = "v9.0.0";
    private const string Setup = "OpenLamina-Windows-Installer.exe";
    private const string SetupSha = "2222222222222222222222222222222222222222222222222222222222222222";

    internal static (ECDsa Key, string PublicKey) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    internal static byte[] Sign(ECDsa key, byte[] data) =>
        key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    [Fact]
    public void Signed_checksums_give_the_installers_sha256()
    {
        var (key, publicKey) = NewKey();
        using (key)
        {
            var signature = Sign(key, SignedSums.SignedMessage(Tag, Sums));
            Assert.Equal(SetupSha, SignedSums.Expected(Tag, Sums, signature, Setup, null, publicKey));
            Assert.Equal(SetupSha, SignedSums.Expected(Tag, Sums, signature, Setup, SetupSha, publicKey));
        }
    }

    [Fact]
    public void Checksums_signed_with_another_key_or_changed_are_refused()
    {
        var (key, publicKey) = NewKey();
        var (other, _) = NewKey();
        using (key)
        using (other)
        {
            Assert.Throws<InvalidDataException>(() => SignedSums.Expected(Tag, Sums, Sign(other, SignedSums.SignedMessage(Tag, Sums)), Setup, null, publicKey));
            var changed = (byte[])Sums.Clone();
            changed[70] ^= 1;
            Assert.Throws<InvalidDataException>(() => SignedSums.Expected(Tag, changed, Sign(key, SignedSums.SignedMessage(Tag, Sums)), Setup, null, publicKey));
            Assert.Throws<InvalidDataException>(() => SignedSums.Expected(Tag, Sums, [], Setup, null, publicKey));
            Assert.Throws<InvalidDataException>(() => SignedSums.Expected(Tag, Sums, Encoding.ASCII.GetBytes("not a signature"), Setup, null, publicKey));
        }
    }

    [Fact]
    public void Another_releases_signed_checksums_are_refused()
    {
        var (key, publicKey) = NewKey();
        using (key)
        {
            var older = Sign(key, SignedSums.SignedMessage("v8.0.0", Sums));
            var e = Assert.Throws<InvalidDataException>(() => SignedSums.Expected(Tag, Sums, older, Setup, null, publicKey));
            Assert.Equal("OpenLamina 9.0.0 was refused: its SHA256SUMS.txt isn't signed for v9.0.0 with OpenLamina's release key.", e.Message);

            Assert.Throws<InvalidDataException>(() => SignedSums.Expected(Tag, Sums, Sign(key, Sums), Setup, null, publicKey));
        }
    }

    [Fact]
    public void Openssl_signatures_are_accepted()
    {
        using var dir = new TempDir();
        string key = dir.File("key.pem"), pub = dir.File("pub.pem"), message = dir.File("message"), sig = dir.File("sig");
        if (!Openssl($"ecparam -name prime256v1 -genkey -noout -out \"{key}\""))
        {
            Console.WriteLine("No openssl here: openssl's signatures weren't tried.");
            return;
        }

        Assert.True(Openssl($"ec -in \"{key}\" -pubout -out \"{pub}\""));
        File.WriteAllBytes(message, SignedSums.SignedMessage(Tag, Sums));
        var publicKey = string.Concat(File.ReadAllLines(pub).Where(l => !l.StartsWith("-----", StringComparison.Ordinal)));

        for (var i = 0; i < 12; i++)
        {
            Assert.True(Openssl($"dgst -sha256 -sign \"{key}\" -out \"{sig}\" \"{message}\""));
            Assert.Equal(SetupSha, SignedSums.Expected(Tag, Sums, File.ReadAllBytes(sig), Setup, SetupSha, publicKey));
        }
    }

    private static bool Openssl(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("openssl", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            })!;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000))
            {
                p.Kill();
                return false;
            }

            Task.WaitAll(output, error);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public void A_file_the_checksums_dont_list_or_another_digest_is_refused()
    {
        var (key, publicKey) = NewKey();
        using (key)
        {
            var signature = Sign(key, SignedSums.SignedMessage(Tag, Sums));
            Assert.Throws<InvalidDataException>(() => SignedSums.Expected(Tag, Sums, signature, "OpenLamina-Linux.tar.gz", null, publicKey));
            Assert.Throws<InvalidDataException>(() => SignedSums.Expected(Tag, Sums, signature, Setup, new string('3', 64), publicKey));
        }
    }

    [Theory]
    [InlineData("MHYwEAYHKoZIzj0CAQYFK4EEACIDYgAE")]
    [InlineData("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE")]
    [InlineData("not base64!")]
    public void Only_a_p256_key_is_taken(string publicKey) =>
        Assert.Throws<InvalidDataException>(() => SignedSums.ParsePublicKey(publicKey));

    [Fact]
    public void A_point_off_the_curve_is_refused_as_a_key()
    {
        var (key, publicKey) = NewKey();
        using (key)
        {
            var der = Convert.FromBase64String(publicKey);
            der[^1] ^= 1;
            Assert.Throws<InvalidDataException>(() =>
                SignedSums.Expected(Tag, Sums, Sign(key, SignedSums.SignedMessage(Tag, Sums)), Setup, null, Convert.ToBase64String(der)));
        }
    }

    [Fact]
    public void The_pinned_key_is_a_p256_key()
    {
        using var key = ECDsa.Create(SignedSums.ParsePublicKey(SignedSums.PublicKey));
        Assert.Equal(256, key.KeySize);
    }

    [Fact]
    public void Der_signatures_become_r_and_s()
    {
        byte[] r = Enumerable.Repeat((byte)0x81, 32).ToArray(), s = Enumerable.Repeat((byte)0x05, 31).ToArray();
        byte[] der = [0x30, 2 + 33 + 2 + 31, 0x02, 33, 0x00, .. r, 0x02, 31, .. s];
        var raw = SignedSums.DerToP1363(der)!;
        Assert.Equal(r, raw[..32]);
        Assert.Equal([0, .. s], raw[32..]);

        Assert.Null(SignedSums.DerToP1363([0x30, 6, 0x02, 1, 0x81, 0x02, 1, 0x01]));
        Assert.Null(SignedSums.DerToP1363([0x30, 7, 0x02, 2, 0x00, 0x01, 0x02, 1, 0x01]));
        Assert.Null(SignedSums.DerToP1363([0x30, 6, 0x02, 1, 0x01, 0x02, 1, 0x01, 0x00]));
        byte[] tooLong = [0x30, 2 + 34 + 3, 0x02, 34, 0x00, .. Enumerable.Repeat((byte)0x81, 33), 0x02, 1, 0x01];
        Assert.Null(SignedSums.DerToP1363(tooLong));
    }

    [Fact]
    public void The_checksums_list_finds_one_file()
    {
        var text = Encoding.UTF8.GetString(Sums);
        Assert.Equal(SetupSha, SignedSums.Find(text, Setup));
        Assert.Null(SignedSums.Find(text, "Installer.exe"));
        Assert.Null(SignedSums.Find(text + SetupSha + "  " + Setup + "\n", Setup));
        Assert.Null(SignedSums.Find(SetupSha + " *" + Setup + "\n", Setup));
    }
}
