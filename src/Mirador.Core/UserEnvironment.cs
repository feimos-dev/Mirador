using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mirador.Core;

public static class UserEnvironment
{
    public static IReadOnlyDictionary<string, string> Default(Action<Exception>? fellBack = null)
    {
        try
        {
            return FromWindows();
        }
        catch (Exception e) when (e is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            fellBack?.Invoke(e);
            return Current();
        }
    }

    public static IReadOnlyDictionary<string, string> Current()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                found[name] = value;
            }
        }

        return found;
    }

    internal static IReadOnlyDictionary<string, string> Parse(string block)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in block.Split('\0'))
        {
            if (entry.Length == 0 || entry[0] == '=')
            {
                continue;
            }

            var equals = entry.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            found[entry[..equals]] = entry[(equals + 1)..];
        }

        return found;
    }

    private static IReadOnlyDictionary<string, string> FromWindows()
    {
        const uint TokenQuery = 0x0008, TokenDuplicate = 0x0002;
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenDuplicate, out var token))
        {
            throw new Win32Exception();
        }

        try
        {
            if (!CreateEnvironmentBlock(out var block, token, inherit: false))
            {
                throw new Win32Exception();
            }

            try
            {
                return Parse(ReadBlock(block));
            }
            finally
            {
                DestroyEnvironmentBlock(block);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static string ReadBlock(IntPtr block)
    {
        var length = 0;
        while (Marshal.ReadInt16(block, length * 2) != 0 || Marshal.ReadInt16(block, (length + 1) * 2) != 0)
        {
            length++;
        }

        return Marshal.PtrToStringUni(block, length + 1);
    }

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
