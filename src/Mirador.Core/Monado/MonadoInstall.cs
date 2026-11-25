namespace Mirador.Core.Monado;

public sealed record MonadoInstall(string Manifest, string RuntimeDll, string ServiceExe, string StandInDir, bool IsBundled, Mirador.Ipc.ApprovedBuild? Approved = null)
{
    public IEnumerable<string> RuntimeManifests => Approved is { } a ? [Manifest, a.Manifest] : [Manifest];

    public IEnumerable<string> StandInDirs => Approved is { } a ? [StandInDir, a.StandInDir] : [StandInDir];
}
