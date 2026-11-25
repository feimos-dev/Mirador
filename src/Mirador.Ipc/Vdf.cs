using System.Text;

namespace Mirador.Ipc;

internal sealed class VdfNode
{
    public VdfNode(string text)
    {
        Text = text;
        Members = [];
    }

    public VdfNode(IReadOnlyList<KeyValuePair<string, VdfNode>> members) => Members = members;

    public string? Text { get; }

    public bool IsObject => Text is null;

    public IReadOnlyList<KeyValuePair<string, VdfNode>> Members { get; }

    public VdfNode? Get(string key)
    {
        foreach (var member in Members)
        {
            if (member.Key == key)
            {
                return member.Value;
            }
        }

        return null;
    }
}

internal static class Vdf
{
    private const int MaxDepth = 64;

    public static VdfNode? Parse(string text)
    {
        var position = 0;
        var members = ReadMembers(text, ref position, depth: 0);
        return members is null ? null : new VdfNode(members);
    }

    private static List<KeyValuePair<string, VdfNode>>? ReadMembers(string text, ref int position, int depth)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        var members = new List<KeyValuePair<string, VdfNode>>();
        while (true)
        {
            SkipWhitespace(text, ref position);
            if (position == text.Length)
            {
                return depth == 0 ? members : null;
            }

            if (text[position] == '}')
            {
                if (depth == 0)
                {
                    return null;
                }

                position++;
                return members;
            }

            var key = ReadString(text, ref position);
            if (key is null)
            {
                return null;
            }

            SkipWhitespace(text, ref position);
            if (position == text.Length)
            {
                return null;
            }

            VdfNode value;
            if (text[position] == '{')
            {
                position++;
                var inner = ReadMembers(text, ref position, depth + 1);
                if (inner is null)
                {
                    return null;
                }

                value = new VdfNode(inner);
            }
            else
            {
                var str = ReadString(text, ref position);
                if (str is null)
                {
                    return null;
                }

                value = new VdfNode(str);
            }

            members.Add(new KeyValuePair<string, VdfNode>(key, value));
        }
    }

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }

    private static string? ReadString(string text, ref int position)
    {
        if (position >= text.Length || text[position] != '"')
        {
            return null;
        }

        position++;
        var result = new StringBuilder();
        while (position < text.Length)
        {
            var c = text[position++];
            if (c == '"')
            {
                return result.ToString();
            }

            if (c == '\\' && position < text.Length && text[position] is '\\' or '"')
            {
                c = text[position++];
            }

            result.Append(c);
        }

        return null;
    }
}
