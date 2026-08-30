using GHelper.Daemon.Contract;
using GHelper.Daemon.Core;
using Tmds.DBus.Protocol;

namespace GHelper.Daemon.DBus;

public sealed class DaemonMethodHandler : IPathMethodHandler
{
    // Long enough for the future interactive polkit seam. Production Phase 2
    // returns before any identity/auth call, so it never consumes this window.
    public static readonly TimeSpan MutationRequestTimeout = TimeSpan.FromSeconds(95);
    private readonly DaemonCore _core;

    public DaemonMethodHandler(DaemonCore core)
        => _core = core ?? throw new ArgumentNullException(nameof(core));

    public string Path => DaemonContract.ObjectPath;
    public bool HandlesChildPaths => false;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([DaemonContract.IntrospectionBytes]);
            return default;
        }

        Message request = context.Request;
        if (!string.Equals(request.InterfaceAsString, DaemonContract.InterfaceName, StringComparison.Ordinal))
        {
            context.ReplyUnknownMethodError();
            return default;
        }

        try
        {
            switch (request.MemberAsString)
            {
                case "GetVersion":
                    RequireSignature(request, string.Empty);
                    ReplyVersion(context);
                    break;
                case "GetCapabilities":
                    RequireSignature(request, string.Empty);
                    ReplyCapabilities(context);
                    break;
                case "GetStatus":
                    RequireSignature(request, string.Empty);
                    ReplyStatus(context);
                    break;
                case "RequestMutation":
                    RequireSignature(request, "s");
                    string operation = request.GetBodyReader().ReadString();
                    context.DisposesAsynchronously = true;
                    _ = HandleMutationAsync(context, request.SenderAsString, operation);
                    break;
                case "StartMutation":
                    RequireSignature(request, "s");
                    string startOperation = request.GetBodyReader().ReadString();
                    context.DisposesAsynchronously = true;
                    _ = HandleStartMutationAsync(context, request.SenderAsString, startOperation);
                    break;
                case "GetMutationStatus":
                    RequireSignature(request, "s");
                    ReplyMutationStatus(context, request.GetBodyReader().ReadString());
                    break;
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
        }
        catch (DaemonRequestException ex)
        {
            ReplyStableError(context, ex.ErrorName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ghelperd request parsing failed ({ex.GetType().Name}).");
            ReplyStableError(context, DaemonContract.ErrorInvalidArguments);
        }
        return default;
    }

    private async Task HandleMutationAsync(MethodContext context, string? sender, string operation)
    {
        using (context)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
        {
            timeout.CancelAfter(MutationRequestTimeout);
            try
            {
                await _core.RequestMutationAsync(sender ?? string.Empty, operation, timeout.Token)
                    .ConfigureAwait(false);
                using MessageWriter writer = context.CreateReplyWriter(string.Empty);
                context.Reply(writer.CreateMessage());
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                ReplyStableError(context, DaemonContract.ErrorCancelled);
            }
            catch (OperationCanceledException)
            {
                ReplyStableError(context, DaemonContract.ErrorTimedOut);
            }
            catch (DaemonRequestException ex)
            {
                ReplyStableError(context, ex.ErrorName);
            }
            catch (Exception)
            {
                Console.Error.WriteLine("ghelperd mutation request failed (internal error)." );
                ReplyStableError(context, DaemonContract.ErrorFailed);
            }
        }
    }

    private async Task HandleStartMutationAsync(MethodContext context, string? sender, string operation)
    {
        using (context)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
        {
            timeout.CancelAfter(MutationRequestTimeout);
            try
            {
                string jobId = await _core.StartMutationAsync(
                    sender ?? string.Empty, operation, timeout.Token).ConfigureAwait(false);
                using MessageWriter writer = context.CreateReplyWriter("s");
                writer.WriteString(jobId);
                context.Reply(writer.CreateMessage());
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                ReplyStableError(context, DaemonContract.ErrorCancelled);
            }
            catch (OperationCanceledException)
            {
                ReplyStableError(context, DaemonContract.ErrorTimedOut);
            }
            catch (DaemonRequestException ex)
            {
                ReplyStableError(context, ex.ErrorName);
            }
            catch (Exception)
            {
                Console.Error.WriteLine("ghelperd mutation start failed (internal error).");
                ReplyStableError(context, DaemonContract.ErrorFailed);
            }
        }
    }

    private static void RequireSignature(Message request, string expected)
    {
        if (!string.Equals(request.SignatureAsString ?? string.Empty, expected, StringComparison.Ordinal))
            throw new DaemonRequestException(
                DaemonContract.ErrorInvalidArguments,
                $"Method '{request.MemberAsString}' requires signature '{expected}'.");
    }

    public static string PublicErrorMessage(string errorName)
        => DaemonContract.GetPublicErrorMessage(errorName);

    private static void ReplyStableError(MethodContext context, string errorName)
        => context.ReplyError(errorName, PublicErrorMessage(errorName));

    private void ReplyVersion(MethodContext context)
    {
        DaemonVersionInfo version = _core.GetVersion();
        using MessageWriter writer = context.CreateReplyWriter("us");
        writer.WriteUInt32(version.ApiVersion);
        writer.WriteString(version.DaemonVersion);
        context.Reply(writer.CreateMessage());
    }

    private void ReplyCapabilities(MethodContext context)
    {
        using MessageWriter writer = context.CreateReplyWriter("as");
        writer.WriteArray(_core.GetCapabilities());
        context.Reply(writer.CreateMessage());
    }

    private void ReplyStatus(MethodContext context)
    {
        DaemonStatus status = _core.GetStatus();
        using MessageWriter writer = context.CreateReplyWriter("ss");
        writer.WriteString(status.State);
        writer.WriteString(status.Detail);
        context.Reply(writer.CreateMessage());
    }

    private void ReplyMutationStatus(MethodContext context, string jobId)
    {
        MutationJobStatus status = _core.GetMutationStatus(jobId);
        using MessageWriter writer = context.CreateReplyWriter("ssss");
        writer.WriteString(status.Operation);
        writer.WriteString(status.State);
        writer.WriteString(status.Detail);
        writer.WriteString(status.ErrorName);
        context.Reply(writer.CreateMessage());
    }
}
