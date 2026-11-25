using System.ComponentModel;
using System.Diagnostics;
using Mirador.Core.Native;

namespace Mirador.Core;

public sealed record ProcInfo(string Name, int Id);

public interface IProcessList
{
    IReadOnlyList<ProcInfo> Snapshot();

    string? ImagePathOf(int id) => null;
}

public sealed class SystemProcessList : IProcessList
{
    public IReadOnlyList<ProcInfo> Snapshot()
    {
        var found = new List<ProcInfo>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    found.Add(new ProcInfo(process.ProcessName, process.Id));
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception)
                {
                }
            }
        }

        return found;
    }

    public string? ImagePathOf(int id) => Win32.ProcessImagePath(id);
}
