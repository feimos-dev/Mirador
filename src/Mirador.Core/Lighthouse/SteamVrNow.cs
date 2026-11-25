using System.Text.Json;
using System.Text.Json.Nodes;
using Mirador.Core.BaseStations;

namespace Mirador.Core.Lighthouse;

public sealed record SteamVrBase(string? Universe, float X, float Y, float Z, float Yaw)
{
    public static SteamVrBase None { get; } = new(null, 0, 0, 0, 0);

    public bool SameAs(SteamVrBase other)
    {
        const float tolerance = 1e-4f;
        return string.Equals(Universe, other.Universe, StringComparison.Ordinal)
            && MathF.Abs(X - other.X) <= tolerance
            && MathF.Abs(Y - other.Y) <= tolerance
            && MathF.Abs(Z - other.Z) <= tolerance
            && MathF.Abs(Yaw - other.Yaw) <= tolerance;
    }
}

public sealed record SteamVrNow(UniversePick Pick, IReadOnlySet<string> RoomSetups, SteamVrBase? Base)
{
    public const string Unreadable = "Couldn't read SteamVR's Lighthouse files; try again.";

    private static readonly JsonDocumentOptions Options = new() { AllowDuplicateProperties = false };

    public static SteamVrNow Read(string? steamDir, IReadOnlyList<KnownStation> stations)
    {
        if (steamDir is null)
        {
            return new SteamVrNow(new UniversePick(null, false), new HashSet<string>(), null);
        }

        var lighthousedb = Text(SteamVrRoom.LighthousedbPath(steamDir));
        var vrchap = Text(SteamVrRoom.ChaperoneInfoPath(steamDir));
        var universes = SteamVrRoom.Universes(lighthousedb);
        var roomSetups = SteamVrRoom.RoomSetups(vrchap);
        var inUse = SteamVrRoom.SteamVrLhPick(universes, roomSetups);
        var standing = inUse is null ? null : SteamVrRoom.Standing(vrchap, inUse);
        var steamVrBase = standing is var (t, yaw) ? new SteamVrBase(inUse, t[0], t[1], t[2], yaw) : SteamVrBase.None;
        return new SteamVrNow(CurrentUniverse.Pick(universes, roomSetups, stations), roomSetups, steamVrBase);
    }

    private static string Text(string path)
    {
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new MiradorException(Unreadable);
        }

        try
        {
            return JsonNode.Parse(text, documentOptions: Options) is JsonObject ? text : throw new MiradorException(Unreadable);
        }
        catch (JsonException)
        {
            throw new MiradorException(Unreadable);
        }
    }
}
