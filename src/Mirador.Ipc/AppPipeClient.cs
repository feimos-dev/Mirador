using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Mirador.Ipc;

public static class AppPipeClient
{
    public static async Task<string?> SendAsync(string name, string line, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(limit.Token).ConfigureAwait(false);
            if (!OwnedByThisUser(pipe))
            {
                return null;
            }

            await pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), limit.Token).ConfigureAwait(false);
            await pipe.FlushAsync(limit.Token).ConfigureAwait(false);
            return await AppPipeServer.ReadLineAsync(pipe, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static bool OwnedByThisUser(NamedPipeClientStream pipe)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && owner == identity.User;
    }
}
