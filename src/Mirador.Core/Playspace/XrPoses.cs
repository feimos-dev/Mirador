using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Playspace;

public sealed record XrPoses(MonadoPose Head, MonadoPose? Left, MonadoPose? Right)
{
    public const string Prefix = "XR-POSES ";

    public const string FailurePrefix = "XR-POSES-FAIL ";

    public static XrPoses? Parse(IEnumerable<string> lines)
    {
        var line = lines.LastOrDefault(l => l.StartsWith(Prefix, StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(line[Prefix.Length..], documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) is not JsonObject root
                || Pose(root["head"]) is not { } head
                || !Hand(root, "left", out var left)
                || !Hand(root, "right", out var right))
            {
                return null;
            }

            return new XrPoses(head, left, right);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Failure(IEnumerable<string> lines) =>
        lines.LastOrDefault(l => l.StartsWith(FailurePrefix, StringComparison.Ordinal))?[FailurePrefix.Length..].Trim();

    private static bool Hand(JsonObject root, string name, out MonadoPose? pose)
    {
        pose = null;
        if (!root.TryGetPropertyValue(name, out var node))
        {
            return false;
        }

        if (node is null)
        {
            return true;
        }

        pose = Pose(node);
        return pose is not null;
    }

    private static MonadoPose? Pose(JsonNode? node)
    {
        if (node is not JsonObject pose || Numbers(pose["p"], 3) is not { } p || Numbers(pose["q"], 4) is not { } q)
        {
            return null;
        }

        var length = (q[0] * q[0]) + (q[1] * q[1]) + (q[2] * q[2]) + (q[3] * q[3]);
        return MathF.Abs(length - 1) < 1e-3f ? new MonadoPose(q[0], q[1], q[2], q[3], p[0], p[1], p[2]) : null;
    }

    private static float[]? Numbers(JsonNode? node, int count)
    {
        if (node is not JsonArray array || array.Count != count)
        {
            return null;
        }

        var numbers = new float[count];
        for (var i = 0; i < count; i++)
        {
            if (array[i] is not JsonValue value
                || value.GetValueKind() != JsonValueKind.Number
                || !float.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])
                || !float.IsFinite(numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }
}
