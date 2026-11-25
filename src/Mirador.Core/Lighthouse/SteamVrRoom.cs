using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Playspace;

namespace Mirador.Core.Lighthouse;

public sealed record LighthouseUniverse(string Id, IReadOnlyList<string> StationSerials);

public static class SteamVrRoom
{
    private static readonly JsonDocumentOptions Options = new() { AllowDuplicateProperties = false };

    public static string LighthousedbPath(string steamDir) => Path.Combine(steamDir, "config", "lighthouse", "lighthousedb.json");

    public static string ChaperoneInfoPath(string steamDir) => Path.Combine(steamDir, "config", "chaperone_info.vrchap");

    public static (IReadOnlyList<LighthouseUniverse> Universes, IReadOnlySet<string> RoomSetups) Read(string? steamDir) =>
        steamDir is null
            ? ([], new HashSet<string>())
            : (Universes(ReadOrEmpty(LighthousedbPath(steamDir))), RoomSetups(ReadOrEmpty(ChaperoneInfoPath(steamDir))));

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

    public static IReadOnlySet<string> RoomSetups(string vrchapJson) =>
        Array(Parse(vrchapJson)?["universes"])
            .Select(entry => entry is JsonObject universe ? Text(universe["universeID"]) : null)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    public static string? SteamVrLhPick(IReadOnlyList<LighthouseUniverse> universes, IReadOnlySet<string> roomSetups) =>
        universes.FirstOrDefault(u => roomSetups.Contains(u.Id))?.Id;

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

    private static string? Text(JsonNode? node) => node switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.GetValue<JsonElement>().GetRawText(),
        _ => null,
    };

    public const string LighthouseOrigin = "SteamVR Lighthouse Tracking";

    public const string StillOtherSpace =
        "SteamVR's room setup of another space is used: the floor and forward direction may be wrong in games. Exporting wouldn't change that: SteamVR would still use the room setup of an earlier space.";

    public const string NoRoomSetupToCopy = "SteamVR has no room setup to copy; run SteamVR's Room Setup once.";

    private const string Unreadable = "SteamVR's room setup file isn't one Mirador can read, so Mirador leaves it as it is.";

    public static (float[] Translation, float Yaw)? Standing(string vrchapJson, string universe)
    {
        return Entries(Parse(vrchapJson)).FirstOrDefault(e => Text(e["universeID"]) == universe) is { } entry ? StandingOf(entry) : null;
    }

    private static (float[] Translation, float Yaw) StandingOf(JsonObject entry)
    {
        var standing = entry["standing"] as JsonObject;
        var given = Array(standing?["translation"]).ToList();
        var translation = new float[3];
        for (var i = 0; i < 3; i++)
        {
            translation[i] = i < given.Count ? Number(given[i]) : 0;
        }

        return (translation, Number(standing?["yaw"]));
    }

    public static MonadoPose ChaperonePose(float[] translation, float yaw)
    {
        var turn = new MonadoPose(0, -MathF.Sin(yaw / 2), 0, MathF.Cos(yaw / 2), 0, 0, 0);
        var at = RoomSetupMath.Transform(turn, new MonadoPose(0, 0, 0, 1, translation[0], translation[1], translation[2]));
        return turn with { X = at.X, Y = at.Y, Z = at.Z };
    }

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
        else
        {
            var (oldTranslation, oldYaw) = StandingOf(entry);
            var old = ChaperonePose(oldTranslation, oldYaw);
            var undo = RoomSetupMath.Transform(
                new MonadoPose(-old.Qx, -old.Qy, -old.Qz, old.Qw, 0, 0, 0), new MonadoPose(0, 0, 0, 1, -old.X, -old.Y, -old.Z));
            var move = RoomSetupMath.Transform(ChaperonePose(translation, yaw), undo);
            foreach (var point in Array(entry["collision_bounds"]).SelectMany(Array).OfType<JsonArray>().Where(p => p.Count == 3))
            {
                var at = RoomSetupMath.Transform(move, new MonadoPose(0, 0, 0, 1, Number(point[0]), Number(point[1]), Number(point[2])));
                point[0] = at.X;
                point[1] = at.Y;
                point[2] = at.Z;
            }
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

        if (ExportRefusal(steamDir, universe) is { } refusal)
        {
            throw new MiradorException(refusal);
        }

        var text = Encoding.UTF8.GetString(original).TrimStart('\uFEFF');
        var inUse = SteamVrLhPick(Read(steamDir).Universes, RoomSetups(text));
        var (translation, yaw) = (inUse is null ? null : Standing(text, inUse)) ?? ([0, 0, 0], 0);
        var (newTranslation, newYaw) = StandingFor(offset, translation, yaw);
        var written = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(WithStanding(text, universe, newTranslation, newYaw));

        Directory.CreateDirectory(backupDir);
        var stamped = Path.Combine(
            backupDir, Path.GetFileName(path) + ".mirador-" + time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var backup = stamped;
        for (var n = 2; File.Exists(backup); n++)
        {
            backup = $"{stamped}-{n}";
        }

        File.WriteAllBytes(backup, original);

        if (!ReplaceIfUnchanged(path, original, written))
        {
            throw new MiradorException("SteamVR's room setup changed while Mirador was exporting, so Mirador left it as it is. Nothing changed.");
        }

        var (universesNow, roomSetupsNow) = Read(steamDir);
        if (SteamVrLhPick(universesNow, roomSetupsNow) != universe)
        {
            const string notUsed = "After the export SteamVR still wouldn't use this room setup";
            throw PutBack(
                path, written, original,
                $"{notUsed}, so Mirador put SteamVR's room setup back and kept its own.",
                why => $"{notUsed}. {why}. Mirador kept its own. SteamVR's room setup as it was is in {backup}.");
        }

        try
        {
            clearOwn();
        }
        catch (Exception e)
        {
            var notCleared = $"Mirador couldn't clear its own copy of the room setup ({e.Message.Trim().TrimEnd('.')})";
            throw PutBack(
                path, written, original,
                $"{notCleared}, so it put SteamVR's room setup back. Nothing changed.",
                why => $"{notCleared}. {why}: both may apply when Monado next starts. SteamVR's room setup as it was is in {backup}.");
        }

        return backup;
    }

    private static MiradorException PutBack(string path, byte[] written, byte[] original, string putBack, Func<string, string> notPutBack)
    {
        try
        {
            return ReplaceIfUnchanged(path, written, original)
                ? new MiradorException(putBack)
                : new MiradorException(notPutBack("SteamVR's room setup changed meanwhile, so Mirador left it as it is"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new MiradorException(notPutBack($"Mirador couldn't put SteamVR's room setup back ({e.Message.Trim().TrimEnd('.')})"));
        }
    }

    private static bool ReplaceIfUnchanged(string path, byte[] expected, byte[] content)
    {
        var temp = path + ".mirador-new";
        try
        {
            File.WriteAllBytes(temp, content);
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
            {
                return false;
            }

            File.Replace(temp, path, destinationBackupFileName: null);
            return true;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public static SteamVrBase BaseNow(string steamDir) => SteamVrNow.Read(steamDir, []).Base!;

    public static bool HasRoomSetup(string steamDir, string universe) => Read(steamDir).RoomSetups.Contains(universe);

    public static string? ExportRefusal(string steamDir, string universe)
    {
        var (universes, roomSetups) = Read(steamDir);
        var withThis = new HashSet<string>(roomSetups, StringComparer.Ordinal) { universe };
        return SteamVrLhPick(universes, withThis) == universe
            ? null
            : StillOtherSpace;
    }

    private static IEnumerable<JsonObject> Entries(JsonObject? root) => Array(root?["universes"]).OfType<JsonObject>();

    private static float Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
        && float.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && float.IsFinite(number)
            ? number
            : 0;
}
