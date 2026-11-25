namespace Mirador.Ipc;

public static class StandInNames
{
    public const string ExecutableName = "mirador-standin";

    public const string FileName = ExecutableName + ".exe";

    public const string ServiceFileName = "monado-service.exe";

    public static IReadOnlyList<string> FileNames { get; } = [FileName, ServiceFileName];
}
