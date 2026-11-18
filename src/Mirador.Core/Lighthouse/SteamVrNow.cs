using System.Text.Json;
using System.Text.Json.Nodes;
using Mirador.Core.BaseStations;

namespace Mirador.Core.Lighthouse;

/// <summary>
/// The room setup Monado's <c>steamvr_lh</c> driver applies: the universe it picks and that universe's
/// <c>standing</c>; <see cref="None"/> when it picks none. A room setup Mirador measures is relative to it.
/// </summary>
/// <param name="Universe">The universe <c>steamvr_lh</c> picks; null when none.</param>
/// <param name="X">The standing translation's x.</param>
/// <param name="Y">The standing translation's y.</param>
/// <param name="Z">The standing translation's z.</param>
/// <param name="Yaw">The standing yaw.</param>
public sealed record SteamVrBase(string? Universe, float X, float Y, float Z, float Yaw)
{
    /// <summary>No SteamVR room setup applied.</summary>
    public static SteamVrBase None { get; } = new(null, 0, 0, 0, 0);

    /// <summary>The same universe and standing, give or take what float rounding leaves.</summary>
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

/// <summary>What SteamVR's Lighthouse files say now.</summary>
/// <param name="Pick">The universe in use (<see cref="CurrentUniverse.Pick"/>).</param>
/// <param name="RoomSetups">The universes with a room setup in SteamVR's file.</param>
/// <param name="Base">The room setup <c>steamvr_lh</c> applies now; null without Steam.</param>
public sealed record SteamVrNow(UniversePick Pick, IReadOnlySet<string> RoomSetups, SteamVrBase? Base)
{
    /// <summary>Why the files weren't used: one is there but isn't SteamVR's JSON (SteamVR may be writing it).</summary>
    public const string Unreadable = "Couldn't read SteamVR's Lighthouse files; try again.";

    private static readonly JsonDocumentOptions Options = new() { AllowDuplicateProperties = false };

    /// <summary>
    /// Reads <c>lighthousedb.json</c> and <c>chaperone_info.vrchap</c> from <paramref name="steamDir"/> (null: no Steam,
    /// nothing in use). A missing file counts as empty.
    /// </summary>
    /// <exception cref="MiradorException">A file is there but can't be read as SteamVR's (<see cref="Unreadable"/>).</exception>
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

    /// <summary>The file's text, "" when it is missing.</summary>
    /// <exception cref="MiradorException">It can't be read, or isn't a JSON object.</exception>
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
