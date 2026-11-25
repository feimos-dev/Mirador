using Mirador.Ipc;

namespace Mirador.Core.Modules;

public enum StatusLevel
{
    Ok,
    Info,
    Warning,
    Error,
}

public sealed record StatusLine(string Key, string Label, StatusLevel Level, string Text);

public sealed record CheckResult(string Label, StatusLevel Level, string Text, string? Fix = null);

public sealed record SessionWarning(string Key, string Text);

public sealed record SessionEnd(string Message, bool Problem);

public interface IDiagnosticsSink
{
    void AddFile(string entryName, string path);

    void AddText(string entryName, string text);
}

public sealed record ModuleContext(AppPaths Paths, IHelperClient Helper, IProcessList Processes, IUsbDevices Usb, TimeProvider Time);
