using Mirador.Ipc;
namespace Mirador.Core.Session;

internal static class MonadoLogs
{
    public static void Prune(string logsDir, string newLog, int keep)
    {
        if (!Directory.Exists(logsDir))
        {
            return;
        }

        var logs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(newLog) };
        try
        {
            foreach (var file in Directory.EnumerateFiles(logsDir, "monado-*"))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".log.1", StringComparison.OrdinalIgnoreCase))
                {
                    logs.Add(name[..^2]);
                }
                else if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                {
                    logs.Add(name);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var old in logs.OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase).Skip(keep))
        {
            Cleanup.TryDelete(Path.Combine(logsDir, old));
            Cleanup.TryDelete(Path.Combine(logsDir, old + ".1"));
        }
    }

}
