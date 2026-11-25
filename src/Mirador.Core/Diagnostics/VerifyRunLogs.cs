namespace Mirador.Core.Diagnostics;

public static class VerifyRunLogs
{
    public const int Kept = 10;

    public static void Prune(string logsDir, IEnumerable<string?> inUse)
    {
        var keep = new HashSet<string>(
            inUse.Where(f => f is not null).Select(f => Path.GetFileName(f!)),
            StringComparer.OrdinalIgnoreCase);
        string[] logs;
        try
        {
            if (!Directory.Exists(logsDir))
            {
                return;
            }

            logs = Directory.GetFiles(logsDir, DiagnosticsZip.VerifyLogPrefix + "*.log");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var old in logs.OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Skip(Kept - 1))
        {
            if (keep.Contains(Path.GetFileName(old)))
            {
                continue;
            }

            try
            {
                File.Delete(old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
