using System.Diagnostics;

namespace Mirador.Ipc;

public static class AppPipe
{
    public const string Start = "START";

    public const string Show = "SHOW";

    public const string Ok = "OK";

    public const string FailPrefix = "FAIL ";

    public const int MaxLineBytes = 256;

    public static string NameFor(int sessionId) => $"mirador-app-{sessionId}";

    public static string CurrentName()
    {
        using var self = Process.GetCurrentProcess();
        return NameFor(self.SessionId);
    }

    public static string OneLine(string text)
    {
        var line = new string(text.Replace("\r\n", " ").Select(c => c is '\r' or '\n' or '\t' ? ' ' : c is >= ' ' and <= '~' ? c : '?').ToArray());
        return line.Length <= MaxLineBytes ? line : line[..(MaxLineBytes - 3)] + "...";
    }
}
