using System.Globalization;

namespace Mirador.Core.BaseStations;

public enum StationVersion
{
    V1,

    V2,
}

public enum StationCommand
{
    On,

    Standby,

    Sleep,
}

public enum StationPower
{
    Unknown,

    Asleep,

    Waking,

    On,
}

public static class StationProtocol
{
    public static readonly Guid V2Service = Guid.Parse("00001523-1212-efde-1523-785feabcd124");

    public static readonly Guid V2PowerCharacteristic = Guid.Parse("00001525-1212-efde-1523-785feabcd124");

    public static readonly Guid V2IdentifyCharacteristic = Guid.Parse("00008421-1212-efde-1523-785feabcd124");

    public static readonly Guid V1Service = Guid.Parse("0000cb00-0000-1000-8000-00805f9b34fb");

    public static readonly Guid V1PowerCharacteristic = Guid.Parse("0000cb01-0000-1000-8000-00805f9b34fb");

    public const ushort ValveCompany = 0x055D;

    private const int V1Length = 20;

    public static byte[] V2IdentifyValue => [0x01];

    public static StationVersion? VersionOf(string? name)
    {
        if (name is null)
        {
            return null;
        }

        if (name.StartsWith("LHB-", StringComparison.Ordinal))
        {
            return StationVersion.V2;
        }

        return name.StartsWith("HTC BS", StringComparison.Ordinal) ? StationVersion.V1 : null;
    }

    public static IReadOnlyList<byte[]> V2Writes(StationCommand command) =>
        command switch
        {
            StationCommand.On => [[0x01]],
            StationCommand.Standby => [[0x02]],
            StationCommand.Sleep => [[0x00]],
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    public const byte StartingState = 0x01;

    public static StationPower AdvertState(ReadOnlySpan<byte> valveData) =>
        AdvertStateByte(valveData) switch
        {
            0x00 => StationPower.Asleep,
            StartingState or 0x02 => StationPower.Waking,
            0x03 => StationPower.On,
            _ => StationPower.Unknown,
        };

    public static byte? AdvertStateByte(ReadOnlySpan<byte> valveData) => valveData.Length < 5 ? null : valveData[4];

    public static byte[] V1Write(StationCommand command, uint? id)
    {
        var bytes = new byte[V1Length];
        switch (command)
        {
            case StationCommand.On:
                bytes[0] = 0x12;
                bytes.AsSpan(4, 4).Fill(0xFF);
                break;
            case StationCommand.Standby:
            case StationCommand.Sleep:
                bytes[0] = 0x12;
                if (id is { } value)
                {
                    bytes[1] = 0x02;
                    bytes[3] = 0x01;
                    BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), value);
                    if (!BitConverter.IsLittleEndian)
                    {
                        bytes.AsSpan(4, 4).Reverse();
                    }
                }
                else
                {
                    bytes[1] = 0x01;
                    bytes[3] = 0x3C;
                    bytes.AsSpan(4, 4).Fill(0xFF);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }

        return bytes;
    }

    public static bool TryParseV1Id(string? text, out uint id)
    {
        id = 0;
        var trimmed = text?.Trim();
        return trimmed is { Length: 8 }
            && trimmed.All(Uri.IsHexDigit)
            && uint.TryParse(trimmed, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id);
    }

    public static string FormatAddress(ulong address)
    {
        var parts = new string[6];
        for (var i = 0; i < 6; i++)
        {
            parts[i] = ((byte)(address >> (8 * (5 - i)))).ToString("X2", CultureInfo.InvariantCulture);
        }

        return string.Join(':', parts);
    }

    public static bool TryParseAddress(string? text, out ulong address)
    {
        address = 0;
        var parts = text?.Split(':');
        if (parts is not { Length: 6 })
        {
            return false;
        }

        ulong result = 0;
        foreach (var part in parts)
        {
            if (part.Length != 2 || !part.All(Uri.IsHexDigit))
            {
                return false;
            }

            result = (result << 8) | byte.Parse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }

        address = result;
        return true;
    }
}
