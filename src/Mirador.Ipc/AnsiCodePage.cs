using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Mirador.Ipc;

public static class AnsiCodePage
{
    private const uint CodePageAnsi = 0;

    public static string RoundTrip(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var size = WideCharToMultiByte(CodePageAnsi, 0, text, text.Length, null, 0, 0, 0);
        if (size <= 0)
        {
            return text;
        }

        var bytes = new byte[size];
        if (WideCharToMultiByte(CodePageAnsi, 0, text, text.Length, bytes, size, 0, 0) != size)
        {
            return text;
        }

        var length = MultiByteToWideChar(CodePageAnsi, 0, bytes, size, null, 0);
        if (length <= 0)
        {
            return text;
        }

        var chars = new char[length];
        return MultiByteToWideChar(CodePageAnsi, 0, bytes, size, chars, length) == length ? new string(chars) : text;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WideCharToMultiByte(
        uint codePage, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string wide, int wideLength, [Out] byte[]? multi,
        int multiLength, nint defaultChar, nint usedDefaultChar);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] multi, int multiLength, [Out] char[]? wide, int wideLength);
}
