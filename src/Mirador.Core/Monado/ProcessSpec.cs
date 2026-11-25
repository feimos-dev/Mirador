namespace Mirador.Core.Monado;

public sealed record ProcessSpec(
    string FileName,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);
