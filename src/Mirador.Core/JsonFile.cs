using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Mirador.Core;

public static class JsonFile
{
    public static T? Load<T>(string path, JsonTypeInfo<T> typeInfo, Func<T, T?>? accept = null) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string text;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            text = reader.ReadToEnd();
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize(text, typeInfo);
        }
        catch (JsonException)
        {
            value = null;
        }

        if (value is not null && accept is not null)
        {
            value = accept(value);
        }

        if (value is null)
        {
            File.Move(path, path + ".bad", overwrite: true);
        }

        return value;
    }

    public static void KeepCopyAsBad(string path) => File.Copy(path, path + ".bad", overwrite: true);

    private const int MoveAttempts = 10;
    private const int MoveRetryMilliseconds = 20;

    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Write(stream, value, typeInfo);
            stream.Flush(flushToDisk: true);
        }

        if (OperatingSystem.IsWindows() && PosixReplace(temp, path))
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                Thread.Sleep(MoveRetryMilliseconds);
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool PosixReplace(string from, string to)
    {
        const uint delete = 0x00010000, synchronize = 0x00100000;
        const uint shareAll = 0x7, openExisting = 3;
        const int fileRenameInfoEx = 22;
        const uint replaceIfExists = 0x1, posixSemantics = 0x2;

        using var handle = CreateFileW(Path.GetFullPath(from), delete | synchronize, shareAll, IntPtr.Zero, openExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return false;
        }

        var name = Path.GetFullPath(to);
        var nameBytes = name.Length * sizeof(char);
        var buffer = new byte[Math.Max(24, 20 + nameBytes + sizeof(char))];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer, replaceIfExists | posixSemantics);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), nameBytes);
        System.Text.Encoding.Unicode.GetBytes(name, buffer.AsSpan(20));
        return SetFileInformationByHandle(handle, fileRenameInfoEx, buffer, (uint)buffer.Length);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, int infoClass, byte[] info, uint size);

    public static string Text<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        using var stream = new MemoryStream();
        Write(stream, value, typeInfo);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write<T>(Stream stream, T value, JsonTypeInfo<T> typeInfo)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = typeInfo.Options.WriteIndented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        JsonSerializer.Serialize(writer, value, typeInfo);
    }
}
