using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mirador.Ipc;

namespace Mirador.Helper;

public sealed class HelperPipeServer : BackgroundService
{
    public const string DefaultPipeName = "mirador-helper";

    private const int MaxInstances = 4;

    private const int MaxReplacementAttempts = 3;

    private const int PipeBusy = unchecked((int)0x800700E7);

    private const int BufferBytes = 2 * (LineCodec.MaxLineBytes + 1);

    private readonly HelperHandler handler;
    private readonly ILogger<HelperPipeServer> logger;
    private readonly IHostApplicationLifetime lifetime;
    private readonly string pipeName;

    private readonly SemaphoreSlim instances = new(MaxInstances);
    private readonly ConcurrentDictionary<Task, byte> serving = new();
    private NamedPipeServerStream? firstPipe;

    public HelperPipeServer(
        HelperHandler handler, ILogger<HelperPipeServer> logger, IHostApplicationLifetime lifetime,
        string pipeName = DefaultPipeName)
    {
        this.handler = handler;
        this.logger = logger;
        this.lifetime = lifetime;
        this.pipeName = pipeName;
    }

    internal TimeSpan ReadLimit { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    internal Func<bool, NamedPipeServerStream>? PipeFactory { get; init; }

    internal static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    internal static NamedPipeServerStream CreatePipe(string pipeName, bool firstInstance) =>
        NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            inBufferSize: BufferBytes,
            outBufferSize: BufferBytes,
            CreatePipeSecurity());

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        instances.Wait(CancellationToken.None);
        firstPipe = NewPipe(firstInstance: true);
        return base.StartAsync(cancellationToken);
    }

    public override void Dispose()
    {
        firstPipe?.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NamedPipeServerStream? listening = firstPipe ?? throw new InvalidOperationException("The pipe is opened by StartAsync.");
        firstPipe = null;
        logger.LogInformation("The helper is listening on {Pipe}.", pipeName);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var accepted = true;
                try
                {
                    await listening.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (IOException e)
                {
                    logger.LogDebug(e, "A caller disconnected before it was accepted.");
                    accepted = false;
                }

                var next = await NextListenerAsync(stoppingToken).ConfigureAwait(false);
                if (next is null)
                {
                    break;
                }

                var current = listening;
                listening = next;
                if (accepted)
                {
                    var task = ServeAsync(current, stoppingToken);
                    serving.TryAdd(task, 0);
                    _ = task.ContinueWith(t => serving.TryRemove(t, out _), TaskScheduler.Default);
                }
                else
                {
                    current.Dispose();
                    instances.Release();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (listening is not null)
            {
                Disconnect(listening);
                listening.Dispose();
            }

            await Task.WhenAll(serving.Keys).ConfigureAwait(false);
        }
    }

    private NamedPipeServerStream NewPipe(bool firstInstance) =>
        PipeFactory is { } factory ? factory(firstInstance) : CreatePipe(pipeName, firstInstance);

    private async Task<NamedPipeServerStream?> NextListenerAsync(CancellationToken ct)
    {
        await instances.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var busyReported = false;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return NewPipe(firstInstance: false);
                }
                catch (IOException e) when (e.HResult == PipeBusy)
                {
                    if (!busyReported)
                    {
                        logger.LogWarning("Every instance of the helper's pipe is in use; waiting for one to be free.");
                        busyReported = true;
                    }

                    attempt--;
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (attempt == MaxReplacementAttempts)
                    {
                        logger.LogError(e, "The helper couldn't open its pipe again after {Attempts} tries; stopping the helper service.", attempt);
                        lifetime.StopApplication();
                        instances.Release();
                        return null;
                    }

                    logger.LogWarning(e, "The helper couldn't open its pipe again; trying again.");
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            instances.Release();
            throw;
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        try
        {
            HelperRequest? request;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                limit.CancelAfter(ReadLimit);
                try
                {
                    request = await LineCodec.ReadAsync<HelperRequest>(pipe, limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    logger.LogWarning("Dropped a connection: no complete request within {Seconds} seconds.", ReadLimit.TotalSeconds);
                    return;
                }
                catch (InvalidDataException e)
                {
                    logger.LogWarning("Dropped a connection: {Reason}", e.Message);
                    return;
                }
                catch (IOException e)
                {
                    logger.LogDebug(e, "A caller disconnected while sending its request.");
                    return;
                }
            }

            if (request is null)
            {
                logger.LogDebug("A caller connected and left without sending a request.");
                return;
            }

            var reply = await handler.HandleAsync(request, ReadCaller(pipe)).ConfigureAwait(false);
            Log(request.Command, reply);

            using var writeLimit = CancellationTokenSource.CreateLinkedTokenSource(stop);
            writeLimit.CancelAfter(ReadLimit);
            await LineCodec.WriteAsync(pipe, reply, writeLimit.Token).ConfigureAwait(false);
            await WaitForHangUpAsync(pipe, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException e)
        {
            logger.LogDebug(e, "A caller disconnected before it read the reply.");
        }
        catch (Exception e)
        {
            logger.LogError(e, "The helper failed to serve a request.");
        }
        finally
        {
            Disconnect(pipe);
            pipe.Dispose();
            instances.Release();
        }
    }

    private CallerInfo? ReadCaller(NamedPipeServerStream pipe)
    {
        try
        {
            SafeAccessTokenHandle? token = null;
            pipe.RunAsClient(() =>
            {
                const uint Query = 0x0008;
                if (OpenThreadToken(new IntPtr(-2), Query, true, out var opened))
                {
                    token = opened;
                }
            });
            using var _ = token;
            if (token is null || token.IsInvalid)
            {
                return null;
            }

            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            var sid = identity.User;
            if (sid is null || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
            {
                return null;
            }

            var session = ProcessIdToSessionId(processId, out var sessionId) ? (int)sessionId : -1;
            return new CallerInfo(sid, session, (int)processId, ImagePathOf(processId));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            logger.LogDebug(e, "The helper couldn't tell who a caller was.");
            return null;
        }
    }

    static HelperPipeServer()
    {
        using var self = WindowsIdentity.GetCurrent();
        _ = self.User;
        _ = OpenThreadToken(new IntPtr(-2), 0x0008, true, out var none);
        none.Dispose();
    }

    private static string? ImagePathOf(uint processId)
    {
        const uint QueryLimitedInformation = 0x1000;
        using var process = OpenProcess(QueryLimitedInformation, false, processId);
        if (process.IsInvalid)
        {
            return null;
        }

        var path = new StringBuilder(1024);
        var size = (uint)path.Capacity;
        return QueryFullProcessImageNameW(process, 0, path, ref size) ? path.ToString(0, (int)size) : null;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenThreadToken(IntPtr thread, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);

    private async Task WaitForHangUpAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(ReadLimit);
        var buffer = new byte[256];
        try
        {
            while (await pipe.ReadAsync(buffer, limit.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
    }

    private static void Disconnect(NamedPipeServerStream pipe)
    {
        try
        {
            pipe.Disconnect();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or ObjectDisposedException)
        {
        }
    }

    private void Log(HelperCommand command, HelperReply reply)
    {
        if (command == HelperCommand.Ping)
        {
            logger.LogDebug("Ping answered.");
        }
        else if (reply.Ok)
        {
            logger.LogInformation("{Command} done.", command);
        }
        else
        {
            logger.LogWarning("{Command} failed: {Error}", command, reply.Error);
        }
    }
}
