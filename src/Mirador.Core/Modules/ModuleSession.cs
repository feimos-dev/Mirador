using Mirador.Core.Profiles;

namespace Mirador.Core.Modules;

public abstract class ModuleSession
{
    public abstract IReadOnlyList<EnvVar> Environment { get; }

    public abstract IReadOnlyDictionary<string, string> RecoveryData { get; }

    public virtual IReadOnlyList<StatusLine> Status => [];

    public event Action? StatusChanged;

    public event Action<SessionWarning>? Warning;

    public event Action<string>? StopRequested;

    public virtual void OnServiceLine(string line)
    {
    }

    public abstract Task EndAsync(CancellationToken ct);

    protected void RaiseStatusChanged() => StatusChanged?.Invoke();

    protected void RaiseWarning(SessionWarning w) => Warning?.Invoke(w);

    protected void RaiseStopRequested(string reason) => StopRequested?.Invoke(reason);
}
