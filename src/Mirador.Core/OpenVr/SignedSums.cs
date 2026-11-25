using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Mirador.Core.OpenVr;

/// <summary>
/// An OpenLamina release's <c>SHA256SUMS.txt</c> and its signature, <c>SHA256SUMS.txt.sig</c>: what OpenLamina's release
/// job makes with <c>openssl dgst -sha256 -sign</c> and its release key, an ECDSA P-256 signature in DER of the line
/// "OpenLamina &lt;tag&gt;" and then <c>SHA256SUMS.txt</c> (<see cref="SignedMessage"/>), so one release's checksums can't
/// pass for another's. Mirador runs OpenLamina's installer only with the SHA-256 these list, and only if they are signed
/// with the key pinned here.
/// </summary>
/// <remarks>
/// The same rules as OpenLamina's own updater (its <c>RuntimeSwitcher/Core/SignedSums.cs</c>), so that both accept and
/// refuse the same releases: keep them alike.
/// </remarks>
public static partial class SignedSums
{
    public const string SumsName = "SHA256SUMS.txt";

    public const string SignatureName = "SHA256SUMS.txt.sig";

    /// <summary>The most either file may be.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>
    /// OpenLamina's release key's public half: the base64 between the PEM lines of OpenLamina's
    /// <c>installer/release-key.pem</c> (its SubjectPublicKeyInfo), which OpenLamina's app and <c>install.sh</c> pin too.
    /// </summary>
    public const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEGlkOmFuwlxKh/FdPbJukNV++m2P8dOp7PlGOisvwXzn70Yw68wxRs0kNZVuogHjZU8VPJ5sxwkjTT2VBICKQaA==";

    /// <summary>What the release job signs: "OpenLamina &lt;tag&gt;\n", then <c>SHA256SUMS.txt</c> as it is.</summary>
    public static byte[] SignedMessage(string tag, byte[] sums) => [.. Encoding.UTF8.GetBytes($"OpenLamina {tag}\n"), .. sums];

    // SubjectPublicKeyInfo of an uncompressed P-256 point: SEQUENCE { SEQUENCE { id-ecPublicKey, prime256v1 },
    // BIT STRING { 04 X Y } }, X and Y 32 bytes each
    private static readonly byte[] P256Prefix =
    [
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08, 0x2a, 0x86, 0x48,
        0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04,
    ];

    /// <summary>
    /// The key's X and Y from its base64 SubjectPublicKeyInfo (spaces and line breaks ignored), or an
    /// <see cref="InvalidDataException"/> for anything but a P-256 key.
    /// </summary>
    public static ECParameters ParsePublicKey(string base64)
    {
        byte[] der;
        try
        {
            der = Convert.FromBase64String(Whitespace().Replace(base64 ?? "", ""));
        }
        catch (FormatException)
        {
            throw new InvalidDataException("OpenLamina's release key isn't base64.");
        }

        if (der.Length != P256Prefix.Length + 64 || !der.AsSpan(0, P256Prefix.Length).SequenceEqual(P256Prefix))
        {
            throw new InvalidDataException("OpenLamina's release key isn't an ECDSA P-256 public key.");
        }

        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = der[P256Prefix.Length..(P256Prefix.Length + 32)],
                Y = der[(P256Prefix.Length + 32)..],
            },
        };
    }

    /// <summary>
    /// A DER ECDSA signature (SEQUENCE { INTEGER r, INTEGER s }) as the 64 bytes r and s that .NET verifies; null if it
    /// isn't one, or either number doesn't fit 32 bytes.
    /// </summary>
    public static byte[]? DerToP1363(byte[]? der)
    {
        if (der is null || der.Length < 8 || der[0] != 0x30)
        {
            return null;
        }

        var at = 1;
        if (!ReadLength(der, ref at, out var seqLength) || at + seqLength != der.Length)
        {
            return null;
        }

        var result = new byte[64];
        for (var i = 0; i < 2; i++)
        {
            if (at >= der.Length || der[at++] != 0x02 || !ReadLength(der, ref at, out var length) || length < 1
                || at + length > der.Length)
            {
                return null;
            }

            // A positive number: a leading zero byte only before a byte with its high bit set
            int start = at, end = at + length;
            if ((der[start] & 0x80) != 0 || (length > 1 && der[start] == 0 && (der[start + 1] & 0x80) == 0))
            {
                return null;
            }

            while (start < end && der[start] == 0)
            {
                start++;
            }

            if (end - start > 32)
            {
                return null;
            }

            Array.Copy(der, start, result, (i * 32) + 32 - (end - start), end - start);
            at = end;
        }

        return at == der.Length ? result : null;
    }

    // The lengths a P-256 signature has: one byte, or 0x81 and one byte of at least 0x80
    private static bool ReadLength(byte[] der, ref int at, out int length)
    {
        length = 0;
        if (at >= der.Length)
        {
            return false;
        }

        var first = der[at++];
        if (first < 0x80)
        {
            length = first;
            return true;
        }

        if (first != 0x81 || at >= der.Length || der[at] < 0x80)
        {
            return false;
        }

        length = der[at++];
        return true;
    }

    /// <summary>Whether <paramref name="signature"/> (DER) signs <paramref name="message"/> with the key.</summary>
    /// <exception cref="InvalidDataException">The key isn't a P-256 key, or its point isn't on the curve.</exception>
    public static bool Verify(byte[] message, byte[]? signature, string publicKey)
    {
        var raw = DerToP1363(signature);
        if (raw is null)
        {
            return false;
        }

        var parameters = ParsePublicKey(publicKey);
        try
        {
            using var key = ECDsa.Create(parameters);
            return key.VerifyData(message, raw, HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException)
        {
            // A point that isn't on the curve
            throw new InvalidDataException("OpenLamina's release key isn't an ECDSA P-256 public key.");
        }
    }

    /// <summary>
    /// The SHA-256 (lower-case hex) <c>SHA256SUMS.txt</c> lists for <paramref name="fileName"/>, as sha256sum writes it
    /// ("&lt;hex&gt;  &lt;name&gt;"); null if it lists none, or more than one.
    /// </summary>
    public static string? Find(string? sums, string fileName)
    {
        var found = (sums ?? "").Split('\n')
            .Select(line => SumLine().Match(line.TrimEnd('\r')))
            .Where(m => m.Success && m.Groups[2].Value == fileName)
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .ToList();
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>
    /// The SHA-256 of <paramref name="fileName"/> from the signed checksums of the release tagged <paramref name="tag"/>,
    /// after checking the signature with <paramref name="publicKey"/>.
    /// </summary>
    /// <param name="githubSha256">GitHub's digest of the file, when it lists one: it must agree.</param>
    /// <exception cref="InvalidDataException">
    /// Why the release is refused: a signature that doesn't match (or is another release's), the file not listed, or a
    /// SHA-256 other than the one GitHub lists. The message is meant for the user as it is.
    /// </exception>
    public static string Expected(string tag, byte[] sums, byte[]? signature, string fileName, string? githubSha256, string publicKey = PublicKey)
    {
        var name = $"OpenLamina {Plain(tag)}";
        if (!Verify(SignedMessage(tag, sums), signature, publicKey))
        {
            throw new InvalidDataException($"{name} was refused: its {SumsName} isn't signed for {tag} with OpenLamina's release key.");
        }

        var sha = Find(Encoding.UTF8.GetString(sums), fileName)
            ?? throw new InvalidDataException($"{name} was refused: its signed {SumsName} doesn't list {fileName}.");
        if (githubSha256 is not null && !string.Equals(githubSha256, sha, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{name} was refused: GitHub lists another SHA-256 for {fileName} than its signed {SumsName}.");
        }

        return sha;
    }

    private static string Plain(string tag) => tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;

    [GeneratedRegex(@"\s")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^([0-9a-fA-F]{64})  (.+)$")]
    private static partial Regex SumLine();
}
