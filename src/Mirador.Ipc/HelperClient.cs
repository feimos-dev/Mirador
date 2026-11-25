using System.IO.Pipes;
using System.Security.Principal;

namespace Mirador.Ipc;

public interface IHelperClient
{
    Task<HelperReply> SendAsync(HelperCommand command, CancellationToken ct);

    Task<HelperReply> SendAsync(HelperRequest request, CancellationToken ct) => SendAsync(request.Command, ct);
}

public sealed class HelperClient(string pipeName = "mirador-helper") : IHelperClient
{
    internal const string NotRunning =
        "Mirador's helper service isn't running. Reinstall Mirador; for a dev build, run tools\\dev-install-helper.ps1 as administrator.";

    internal const string TimedOut = "Mirador's helper service didn't answer in time.";

    private const string Unreadable =
        "Mirador's helper service sent an answer Mirador couldn't read. Reinstall Mirador.";

    internal TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    internal TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal TimeSpan InstallTimeout { get; init; } = TimeSpan.FromMinutes(15);

    public Task<HelperReply> SendAsync(HelperCommand command, CancellationToken ct) =>
        SendAsync(new HelperRequest(command), ct);

    public async Task<HelperReply> SendAsync(HelperRequest request, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new HelperReply(false, NotRunning) { NotDelivered = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new HelperReply(false, $"Mirador couldn't talk to its helper service ({e.Message.TrimEnd('.')}). Reinstall Mirador.") { NotDelivered = true };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Command == HelperCommand.InstallMonado ? InstallTimeout : ReplyTimeout);
        try
        {
            await LineCodec.WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
            return await LineCodec.ReadAsync<HelperReply>(pipe, timeout.Token).ConfigureAwait(false)
                ?? new HelperReply(false, Unreadable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HelperReply(false, TimedOut);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return new HelperReply(false, Unreadable);
        }
    }
}
