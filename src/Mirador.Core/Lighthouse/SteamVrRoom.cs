using System.Text.Json;
using System.Text.Json.Nodes;

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
}
