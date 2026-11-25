using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Mirador.Core.Native;

internal static class Win32
{
    private const int ErrorInsufficientBuffer = 122;

    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static readonly int[] PathCapacities = [1024, 32768];

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadErrorMode(uint mode, out uint oldMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    public static (uint Volume, ulong Index)? FileIdentity(string path)
    {
        try
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(file, out var information)
                ? (information.VolumeSerialNumber, ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private const uint SemFailCriticalErrors = 0x0001;

    public static T WithoutErrorBoxes<T>(Func<T> action)
    {
        var quiet = SetThreadErrorMode(SemFailCriticalErrors, out var old);
        try
        {
            return action();
        }
        finally
        {
            if (quiet)
            {
                SetThreadErrorMode(old, out _);
            }
        }
    }

    public static string? ProcessImagePath(int id)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, unchecked((uint)id));
        if (process.IsInvalid)
        {
            return null;
        }

        foreach (var capacity in PathCapacities)
        {
            var buffer = new char[capacity];
            var size = (uint)capacity;
            if (QueryFullProcessImageName(process, 0, buffer, ref size))
            {
                return new string(buffer, 0, (int)size);
            }

            if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
            {
                return null;
            }
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

}
