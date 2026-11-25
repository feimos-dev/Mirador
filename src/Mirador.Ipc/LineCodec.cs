using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Mirador.Ipc;

public static class LineCodec
{
    public const int MaxLineBytes = 4096;

    public static int LengthOf<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, TypeInfo<T>()).Length;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, TypeInfo<T>());
        if (json.Length > MaxLineBytes)
        {
            throw new InvalidDataException($"A message is longer than {MaxLineBytes} bytes.");
        }

        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        await stream.WriteAsync(line, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var typeInfo = TypeInfo<T>();
        var line = new byte[MaxLineBytes];
        var one = new byte[1];
        var count = 0;
        while (true)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0)
            {
                if (count == 0)
                {
                    return default;
                }

                throw new InvalidDataException("A message ended before its newline.");
            }

            if (one[0] == (byte)'\n')
            {
                break;
            }

            if (count == MaxLineBytes)
            {
                throw new InvalidDataException($"A message is longer than {MaxLineBytes} bytes.");
            }

            line[count++] = one[0];
        }

        try
        {
            return JsonSerializer.Deserialize(new ReadOnlySpan<byte>(line, 0, count), typeInfo);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("A message is not valid.", e);
        }
    }

    private static JsonTypeInfo<T> TypeInfo<T>() =>
        HelperJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException($"{typeof(T).Name} is not part of the helper protocol.");
}
