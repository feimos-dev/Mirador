namespace Mirador.Ipc;

public static class ServiceImage
{
    public static string? ProgramOf(string? imagePath)
    {
        var text = imagePath?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close < 0 ? text[1..] : text[1..close];
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe < 0 ? text : text[..(exe + 4)];
    }
}
