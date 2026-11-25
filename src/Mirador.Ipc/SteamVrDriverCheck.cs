using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Mirador.Ipc;

public static class SteamVrDriverCheck
{
    private const int NamesShown = 200;

    public static string? Find(string? steamPath, Func<string, string?> readFile, Func<string, bool> dirExists) =>
        SteamVr.FindInstall(steamPath, steamVrPathEnv: null, readFile, dirExists);

    public static SteamVrDriverHold Verify(
        string steamVrPath, Func<string, string?> signer, IReadOnlyDictionary<string, string> approved) =>
        Verify(steamVrPath, signer, approved, FinalPath);

    internal static SteamVrDriverHold Verify(
        string steamVrPath, Func<string, string?> signer, IReadOnlyDictionary<string, string> approved, Func<FileStream, string?> finalPath)
    {
        var win64 = SteamVr.DriverLighthouseBinDir(steamVrPath);
        if (!Directory.Exists(win64))
        {
            return Refuse($"SteamVR's Lighthouse driver folder is missing ({win64}). Install or repair SteamVR.");
        }

        string[] dlls;
        try
        {
            dlls = Dlls(win64);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refuse($"Mirador couldn't read SteamVR's driver folder {win64} ({Trim(e.Message)}). Install or repair SteamVR.");
        }

        if (dlls.Length == 0)
        {
            return Refuse($"SteamVR has no Lighthouse driver in {win64}. Install or repair SteamVR.");
        }

        var holder = new Holder();
        var held = new List<(string Name, FileStream Stream)>();
        foreach (var dll in dlls)
        {
            var name = Path.GetFileName(dll);
            try
            {
                var stream = Open(dll);
                holder.Add(stream);
                held.Add((name, stream));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                holder.Dispose();
                return Refuse($"Mirador couldn't open SteamVR's driver file {name} to check it ({Trim(e.Message)}).");
            }
        }

        var (realPaths, realSteamVr, problem) = Resolve(held, win64, finalPath);
        if (problem is not null || realPaths is null || realSteamVr is null)
        {
            holder.Dispose();
            return Refuse(problem ?? $"Mirador couldn't tell where SteamVR's Lighthouse driver really is ({win64}).");
        }

        var changed = new List<string>();
        for (var i = 0; i < held.Count; i++)
        {
            var (name, stream) = held[i];
            if (IsValve(signer(realPaths[i])))
            {
                continue;
            }

            string sha256;
            try
            {
                sha256 = Sha256(stream);
            }
            catch (IOException e)
            {
                holder.Dispose();
                return Refuse($"Mirador couldn't read SteamVR's driver file {name} to check it ({Trim(e.Message)}).");
            }

            if (!approved.TryGetValue(name, out var allowed) || !string.Equals(allowed, sha256, StringComparison.Ordinal))
            {
                changed.Add(name);
            }
        }

        if (changed.Count > 0)
        {
            holder.Dispose();
            return new SteamVrDriverHold(new Holder(), new HelperReply(
                false,
                $"SteamVR's Lighthouse driver changed since you allowed Monado as administrator ({Names(changed)}).",
                Refusal: HelperRefusal.SteamVrChanged), null);
        }

        return new SteamVrDriverHold(holder, null, realSteamVr);
    }

    private static (string[]? RealPaths, string? SteamVr, string? Problem) Resolve(
        List<(string Name, FileStream Stream)> held, string win64, Func<FileStream, string?> finalPath)
    {
        var realPaths = new string[held.Count];
        for (var i = 0; i < held.Count; i++)
        {
            if (WithoutPrefix(finalPath(held[i].Stream)) is not { Length: > 0 } real)
            {
                return (null, null, $"Mirador couldn't tell where SteamVR's driver file {held[i].Name} really is.");
            }

            realPaths[i] = real;
        }

        var driver = held.FindIndex(h => string.Equals(h.Name, "driver_lighthouse.dll", StringComparison.OrdinalIgnoreCase));
        if (driver < 0)
        {
            return (null, null, $"SteamVR has no driver_lighthouse.dll in {win64}. Install or repair SteamVR.");
        }

        var realWin64 = Path.GetDirectoryName(realPaths[driver]);
        var bin = Parent(realWin64, "win64");
        var lighthouse = Parent(bin, "bin");
        var drivers = Parent(lighthouse, "lighthouse");
        var steamVr = Parent(drivers, "drivers");
        if (realWin64 is null || steamVr is null)
        {
            return (null, null, $"SteamVR's Lighthouse driver isn't in a SteamVR folder ({realWin64 ?? realPaths[driver]}). Install or repair SteamVR.");
        }

        for (var i = 0; i < held.Count; i++)
        {
            var name = held[i].Name;
            if (!string.Equals(Path.GetDirectoryName(realPaths[i]), realWin64, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(realPaths[i]), name, StringComparison.OrdinalIgnoreCase))
            {
                return (null, null,
                    $"SteamVR's driver file {name} is a link to another file ({realPaths[i]}), so Mirador won't run Monado as administrator with it. Install or repair SteamVR.");
            }
        }

        return (realPaths, steamVr, null);
    }

    private static string? Parent(string? folder, string expected) =>
        folder is not null && string.Equals(Path.GetFileName(folder), expected, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(folder)
            : null;

    private static string? WithoutPrefix(string? path)
    {
        if (path is null)
        {
            return null;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private static string? FinalPath(FileStream held)
    {
        const uint FileNameNormalized = 0;
        const uint VolumeNameDos = 0;
        var buffer = new char[1024];
        while (true)
        {
            var length = GetFinalPathNameByHandleW(held.SafeFileHandle, buffer, (uint)buffer.Length, FileNameNormalized | VolumeNameDos);
            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                return new string(buffer, 0, (int)length);
            }

            if (length > 32768)
            {
                return null;
            }

            buffer = new char[length + 1];
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        Microsoft.Win32.SafeHandles.SafeFileHandle file, [System.Runtime.InteropServices.Out] char[] path, uint length, uint flags);

    public static IReadOnlyDictionary<string, string> UnsignedFiles(string steamVrPath, Func<string, string?> signer)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var win64 = SteamVr.DriverLighthouseBinDir(steamVrPath);
        if (!Directory.Exists(win64))
        {
            return files;
        }

        string[] dlls;
        try
        {
            dlls = Dlls(win64);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Mirador couldn't read SteamVR's driver folder {win64} ({Trim(e.Message)}), so Monado was not allowed to run as administrator. Try again; if it keeps failing, repair SteamVR.", e);
        }

        using var holder = new Holder();
        foreach (var dll in dlls)
        {
            var name = Path.GetFileName(dll);
            try
            {
                var held = Open(dll);
                holder.Add(held);
                if (!IsValve(signer(dll)))
                {
                    files[name] = Sha256(held);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Mirador couldn't open SteamVR's driver file {name} to check it ({Trim(e.Message)}), so Monado was not allowed to run as administrator. Close SteamVR and Steam, then try again.", e);
            }
        }

        return files;
    }

    internal const string ValveName = "Valve Corp.";

    internal static bool IsValve(string? subject) => SignerName.Is(subject, ValveName);

    private static string[] Dlls(string win64) =>
        [.. Directory.GetFiles(win64, "*.dll").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];

    private static FileStream Open(string dll) => new(dll, FileMode.Open, FileAccess.Read, FileShare.Read);

    private static string Sha256(FileStream held)
    {
        held.Position = 0;
        var hash = SHA256.HashData(held);
        held.Position = 0;
        return Convert.ToHexStringLower(hash);
    }

    private static string Names(List<string> names)
    {
        var shown = new List<string>();
        var length = 0;
        foreach (var name in names)
        {
            if (shown.Count > 0 && length + name.Length > NamesShown)
            {
                break;
            }

            shown.Add(name);
            length += name.Length + 2;
        }

        var text = string.Join(", ", shown);
        return shown.Count < names.Count ? $"{text} and {names.Count - shown.Count} more" : text;
    }

    private static SteamVrDriverHold Refuse(string message) => new(new Holder(), new HelperReply(false, message), null);

    private static string Trim(string message) => message.Trim().TrimEnd('.');

    private sealed class Holder : IDisposable
    {
        private readonly List<FileStream> handles = [];

        public void Add(FileStream handle) => handles.Add(handle);

        public void Dispose()
        {
            foreach (var handle in handles)
            {
                try
                {
                    handle.Dispose();
                }
                catch (IOException)
                {
                }
            }

            handles.Clear();
        }
    }
}

public sealed record SteamVrDriverHold(IDisposable Hold, HelperReply? Refusal, string? SteamVrPath)
{
    public void Deconstruct(out IDisposable hold, out HelperReply? refusal)
    {
        hold = Hold;
        refusal = Refusal;
    }
}

public static class SignerName
{
    private const string CommonNameOid = "2.5.4.3";
    private const string OrganizationOid = "2.5.4.10";

    public static bool Is(string? subject, string organisation)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        X500DistinguishedName name;
        try
        {
            name = new X500DistinguishedName(subject);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            return false;
        }

        var commonNames = new List<string?>();
        var organizations = new List<string?>();
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.HasMultipleElements)
            {
                return false;
            }

            switch (rdn.GetSingleElementType().Value)
            {
                case CommonNameOid:
                    commonNames.Add(rdn.GetSingleElementValue());
                    break;
                case OrganizationOid:
                    organizations.Add(rdn.GetSingleElementValue());
                    break;
            }
        }

        return commonNames.Count == 1 && organizations.Count == 1 && commonNames[0] == organisation && organizations[0] == organisation;
    }
}
