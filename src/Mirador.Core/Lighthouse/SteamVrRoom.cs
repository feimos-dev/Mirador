using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Playspace;

namespace Mirador.Core.Lighthouse;

/// <summary>A Lighthouse universe SteamVR knows: its id and the serials of its base stations.</summary>
/// <param name="Id">The universe id as the file writes it (a number or a string), so ids compare as text.</param>
/// <param name="StationSerials">The base stations' serials, in the file's order.</param>
public sealed record LighthouseUniverse(string Id, IReadOnlyList<string> StationSerials);

/// <summary>
/// SteamVR's room data: the known Lighthouse universes in <c>&lt;Steam&gt;\config\lighthouse\lighthousedb.json</c> and
/// the room setups per universe in <c>&lt;Steam&gt;\config\chaperone_info.vrchap</c>. Only reads; a file that isn't
/// what SteamVR writes gives empty results.
/// </summary>
public static class SteamVrRoom
{
    private static readonly JsonDocumentOptions Options = new() { AllowDuplicateProperties = false };

    /// <summary><c>&lt;Steam&gt;\config\lighthouse\lighthousedb.json</c>.</summary>
    public static string LighthousedbPath(string steamDir) => Path.Combine(steamDir, "config", "lighthouse", "lighthousedb.json");

    /// <summary><c>&lt;Steam&gt;\config\chaperone_info.vrchap</c>.</summary>
    public static string ChaperoneInfoPath(string steamDir) => Path.Combine(steamDir, "config", "chaperone_info.vrchap");

    /// <summary>
    /// The universes and the room setups in Steam's files as they are now. A file that is missing or can't be read
    /// counts as empty; without Steam (<paramref name="steamDir"/> null) there are none.
    /// </summary>
    public static (IReadOnlyList<LighthouseUniverse> Universes, IReadOnlySet<string> RoomSetups) Read(string? steamDir) =>
        steamDir is null
            ? ([], new HashSet<string>())
            : (Universes(ReadOrEmpty(LighthousedbPath(steamDir))), RoomSetups(ReadOrEmpty(ChaperoneInfoPath(steamDir))));

    /// <summary>
    /// The universes in <c>known_universes</c>, in the file's order, with the <c>base_serial_number</c> of each entry
    /// in their <c>base_stations</c>. Entries without an id are left out.
    /// </summary>
    public static IReadOnlyList<LighthouseUniverse> Universes(string lighthousedbJson)
    {
        var universes = new List<LighthouseUniverse>();
        foreach (var entry in Array(Parse(lighthousedbJson)?["known_universes"]))
        {
            if (entry is JsonObject universe && Text(universe["id"]) is { } id)
            {
                var serials = Array(universe["base_stations"])
                    .Select(station => station is JsonObject s ? Text(s["base_serial_number"]) : null)
                    .OfType<string>()
                    .ToList();
                universes.Add(new LighthouseUniverse(id, serials));
            }
        }

        return universes;
    }

    /// <summary>The <c>universeID</c> of every entry in <c>universes</c>: the universes that have a room setup.</summary>
    public static IReadOnlySet<string> RoomSetups(string vrchapJson) =>
        Array(Parse(vrchapJson)?["universes"])
            .Select(entry => entry is JsonObject universe ? Text(universe["universeID"]) : null)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The universe Monado's <c>steamvr_lh</c> driver takes its room setup from: the first known universe, in the
    /// database's order, that has a room setup. Null when none has one.
    /// </summary>
    public static string? SteamVrLhPick(IReadOnlyList<LighthouseUniverse> universes, IReadOnlySet<string> roomSetups) =>
        universes.FirstOrDefault(u => roomSetups.Contains(u.Id))?.Id;

    /// <summary>The file's text; empty when it is missing or can't be read. Shared, as SteamVR may be writing it.</summary>
    private static string ReadOrEmpty(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "";
        }
    }

    /// <summary>The file's root object; null when the text isn't a JSON object.</summary>
    private static JsonObject? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: Options) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonNode?> Array(JsonNode? node) => node as JsonArray ?? [];

    /// <summary>A string's value, or a number as it is written in the file; null for anything else.</summary>
    private static string? Text(JsonNode? node) => node switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.GetValue<JsonElement>().GetRawText(),
        _ => null,
    };

    /// <summary>The name upstream Monado's <c>steamvr_lh</c> driver gives its tracking origin.</summary>
    public const string LighthouseOrigin = "SteamVR Lighthouse Tracking";

    /// <summary>Why an export can't add a room setup: SteamVR's file has no entry to copy the structure of.</summary>
    public const string NoRoomSetupToCopy = "SteamVR has no room setup to copy; run SteamVR's Room Setup once.";

    private const string Unreadable = "SteamVR's room setup file isn't one Mirador can read, so Mirador leaves it as it is.";

    /*
     * How SteamVR's room setup and Mirador's offset combine (steamvr_lh's init_chaperone and Monado's space overseer):
     *
     *   chaperone = (orientation: standing.yaw about (0, −1, 0), i.e. −yaw about +Y;
     *                position: standing.translation turned by that orientation)
     *   device in origin = chaperone * device as the driver tracks it          (steamvr_lh, math_pose_transform)
     *   device in stage  = offset * device in origin                           (the libmonado origin offset)
     *
     * So with Mirador's offset O on top of SteamVR's chaperone C, devices are placed by O * C. Exporting writes the
     * standing whose chaperone is O * C and then drops O: the yaw becomes standing.yaw minus O's turn about +Y, and the
     * translation is O * C's position turned back by the new orientation. An offset that tilts (turns about another
     * axis than +Y) can't be written: a standing holds a turn about +Y only.
     */

    /// <summary>
    /// The universe's <c>standing</c> as <c>steamvr_lh</c> reads it (missing numbers are 0); null when SteamVR has no
    /// entry for the universe, or the text isn't a JSON object.
    /// </summary>
    public static (float[] Translation, float Yaw)? Standing(string vrchapJson, string universe)
    {
        if (Entries(Parse(vrchapJson)).FirstOrDefault(e => Text(e["universeID"]) == universe) is not { } entry)
        {
            return null;
        }

        var standing = entry["standing"] as JsonObject;
        var given = Array(standing?["translation"]).ToList();
        var translation = new float[3];
        for (var i = 0; i < 3; i++)
        {
            translation[i] = i < given.Count ? Number(given[i]) : 0;
        }

        return (translation, Number(standing?["yaw"]));
    }

    /// <summary>The pose <c>steamvr_lh</c> puts its devices through for a standing <paramref name="translation"/> and <paramref name="yaw"/>.</summary>
    public static MonadoPose ChaperonePose(float[] translation, float yaw)
    {
        var turn = new MonadoPose(0, -MathF.Sin(yaw / 2), 0, MathF.Cos(yaw / 2), 0, 0, 0);
        var at = RoomSetupMath.Transform(turn, new MonadoPose(0, 0, 0, 1, translation[0], translation[1], translation[2]));
        return turn with { X = at.X, Y = at.Y, Z = at.Z };
    }

    /// <summary>
    /// The standing that places the devices as <paramref name="offset"/> on top of the standing (<paramref name="translation"/>,
    /// <paramref name="yaw"/>) does, with no offset: what an export writes.
    /// </summary>
    /// <exception cref="MiradorException">The offset tilts the space.</exception>
    public static (float[] Translation, float Yaw) StandingFor(MonadoPose offset, float[] translation, float yaw)
    {
        if (MathF.Abs(offset.Qx) > 1e-3f || MathF.Abs(offset.Qz) > 1e-3f)
        {
            throw new MiradorException("This room setup tilts the space, which SteamVR's room setup can't hold.");
        }

        var placed = RoomSetupMath.Transform(offset, ChaperonePose(translation, yaw));
        var newYaw = 2 * MathF.Atan2(-placed.Qy, placed.Qw);
        newYaw = MathF.IEEERemainder(newYaw, 2 * MathF.PI);
        var back = new MonadoPose(0, MathF.Sin(newYaw / 2), 0, MathF.Cos(newYaw / 2), 0, 0, 0);
        var local = RoomSetupMath.Transform(back, new MonadoPose(0, 0, 0, 1, placed.X, placed.Y, placed.Z));
        return ([local.X, local.Y, local.Z], newYaw);
    }

    /// <summary>
    /// <paramref name="vrchapJson"/> with the universe's <c>standing.translation</c> and <c>standing.yaw</c> set. A
    /// universe without an entry gets a copy of the first entry with only its <c>universeID</c> and <c>standing</c>
    /// changed. Everything else is kept as it was.
    /// </summary>
    /// <exception cref="MiradorException">The text isn't SteamVR's file, or it has no entry to copy.</exception>
    public static string WithStanding(string vrchapJson, string universe, float[] translation, float yaw)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(vrchapJson, documentOptions: Options) as JsonObject ?? throw new MiradorException(Unreadable);
        }
        catch (JsonException)
        {
            throw new MiradorException(Unreadable);
        }

        var entries = Entries(root).ToList();
        var entry = entries.FirstOrDefault(e => Text(e["universeID"]) == universe);
        if (entry is null)
        {
            var first = entries.FirstOrDefault() ?? throw new MiradorException(NoRoomSetupToCopy);
            entry = first.DeepClone().AsObject();
            entry["universeID"] = universe;
            ((JsonArray)root["universes"]!).Add(entry);
        }

        if (entry["standing"] is not JsonObject standing)
        {
            standing = new JsonObject();
            entry["standing"] = standing;
        }

        standing["translation"] = new JsonArray([.. translation.Select(v => (JsonNode?)JsonValue.Create(v))]);
        standing["yaw"] = yaw;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>
    /// Writes <paramref name="offset"/>, applied on top of SteamVR's room setup of <paramref name="universe"/>, into
    /// SteamVR's file, then calls <paramref name="clearOwn"/> to drop Mirador's own copy, so that never both apply. The
    /// file is copied to <c>chaperone_info.vrchap.mirador-&lt;yyyyMMdd-HHmmss&gt;</c> in <paramref name="backupDir"/>
    /// first, and written aside, then moved over the old one. When the clear fails, SteamVR's file is put back. SteamVR
    /// rewrites the file when it exits: the caller exports only while SteamVR is closed.
    /// </summary>
    /// <returns>The copy of the file as it was.</returns>
    /// <exception cref="MiradorException">Nothing was changed, or (said so) both apply.</exception>
    public static string Export(string steamDir, string backupDir, string universe, MonadoPose offset, Action clearOwn, TimeProvider time)
    {
        var path = ChaperoneInfoPath(steamDir);
        byte[] original;
        try
        {
            original = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new MiradorException(NoRoomSetupToCopy);
        }

        var text = Encoding.UTF8.GetString(original);
        var (translation, yaw) = Standing(text, universe) ?? ([0, 0, 0], 0);
        var (newTranslation, newYaw) = StandingFor(offset, translation, yaw);
        var written = WithStanding(text, universe, newTranslation, newYaw);

        Directory.CreateDirectory(backupDir);
        var backup = Path.Combine(
            backupDir, Path.GetFileName(path) + ".mirador-" + time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        if (!File.Exists(backup))
        {
            File.WriteAllBytes(backup, original);
        }

        var temp = path + ".mirador-new";
        File.WriteAllText(temp, written, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            File.Replace(temp, path, destinationBackupFileName: null);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        try
        {
            clearOwn();
        }
        catch (Exception e)
        {
            var why = e.Message.Trim().TrimEnd('.');
            try
            {
                File.WriteAllBytes(path, original);
            }
            catch (Exception putBack) when (putBack is IOException or UnauthorizedAccessException)
            {
                throw new MiradorException(
                    $"Mirador couldn't clear its own copy of the room setup ({why}) or put SteamVR's room setup back ({putBack.Message.Trim().TrimEnd('.')}): both apply when Monado next starts. SteamVR's room setup as it was is in {backup}.");
            }

            throw new MiradorException($"Mirador couldn't clear its own copy of the room setup ({why}), so it put SteamVR's room setup back. Nothing changed.");
        }

        return backup;
    }

    /// <summary>The object entries of <c>universes</c>.</summary>
    private static IEnumerable<JsonObject> Entries(JsonObject? root) => Array(root?["universes"]).OfType<JsonObject>();

    /// <summary>A number's value; 0 for anything else, as the driver reads it.</summary>
    private static float Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
        && float.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && float.IsFinite(number)
            ? number
            : 0;
}
