using GHelper.Daemon.Contract;
using Tmds.DBus.Protocol;

namespace GHelper.Linux.Daemon;

/// <summary>
/// Non-privileged GUI-side client for the versioned system D-Bus boundary.
/// Phase 2 does not construct or call this type from application startup.
/// </summary>
public sealed class GHelperDaemonClient : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MutationTimeout = TimeSpan.FromSeconds(100);
    private readonly DBusConnection _connection;
    private readonly bool _ownsConnection;

    public bool OwnsConnection => _ownsConnection;

    public GHelperDaemonClient(DBusConnection connection, bool ownsConnection = false)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _ownsConnection = ownsConnection;
    }

    public static async Task<GHelperDaemonClient> ConnectSystemAsync(
        CancellationToken cancellationToken = default)
    {
        string address = DBusAddress.System
            ?? throw new InvalidOperationException("The system D-Bus address is unavailable.");
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().AsTask()
                .WaitAsync(DefaultTimeout, cancellationToken).ConfigureAwait(false);
            return new GHelperDaemonClient(connection, ownsConnection: true);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public Task<DaemonVersionInfo> GetVersionAsync(CancellationToken cancellationToken = default)
        => CallAsync(
            "GetVersion",
            signature: null,
            static message =>
            {
                var reader = message.GetBodyReader();
                return new DaemonVersionInfo(reader.ReadUInt32(), reader.ReadString());
            },
            cancellationToken);

    public Task<string[]> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => CallAsync(
            "GetCapabilities",
            signature: null,
            static message => message.GetBodyReader().ReadArrayOfString(),
            cancellationToken);

    public Task<DaemonStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => CallAsync(
            "GetStatus",
            signature: null,
            static message =>
            {
                var reader = message.GetBodyReader();
                return new DaemonStatus(reader.ReadString(), reader.ReadString());
            },
            cancellationToken);

    public async Task RequestMutationAsync(
        string operation,
        CancellationToken cancellationToken = default)
    {
        if (!DaemonContract.TryGetMutation(operation, out _))
            throw new ArgumentException("Mutation is not in the versioned allowlist.", nameof(operation));

        MessageBuffer request = CreateMutationCall(operation);
        await _connection.CallMethodAsync(request)
            .WaitAsync(MutationTimeout, cancellationToken).ConfigureAwait(false);
    }

    public Task<string> StartMutationAsync(
        string operation,
        CancellationToken cancellationToken = default)
    {
        if (!DaemonContract.TryGetMutation(operation, out _))
            throw new ArgumentException("Mutation is not in the versioned allowlist.", nameof(operation));
        return CallWithStringAsync(
            "StartMutation",
            operation,
            static message => message.GetBodyReader().ReadString(),
            MutationTimeout,
            cancellationToken);
    }

    public Task<MutationJobStatus> GetMutationStatusAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        if (!DaemonContract.IsValidJobId(jobId))
            throw new ArgumentException("Mutation job identifier is invalid.", nameof(jobId));
        return CallWithStringAsync(
            "GetMutationStatus",
            jobId,
            static message =>
            {
                var reader = message.GetBodyReader();
                return new MutationJobStatus(
                    reader.ReadString(),
                    reader.ReadString(),
                    reader.ReadString(),
                    reader.ReadString());
            },
            DefaultTimeout,
            cancellationToken);
    }

    public async Task<MutationJobStatus> WaitForMutationAsync(
        string jobId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            MutationJobStatus status = await GetMutationStatusAsync(jobId, cancellationToken)
                .ConfigureAwait(false);
            if (DaemonContract.IsTerminalMutationState(status.State))
                return status;
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken)
                .ConfigureAwait(false);
        }
        throw new TimeoutException("The daemon did not report a terminal mutation state.");
    }

    public void Dispose()
    {
        if (_ownsConnection)
            _connection.Dispose();
    }

    private async Task<T> CallAsync<T>(
        string member,
        string? signature,
        Func<Message, T> readBody,
        CancellationToken cancellationToken)
    {
        MessageBuffer request = CreateCallMessage(member, signature);
        Task<T> call = _connection.CallMethodAsync(
            request,
            static (Message message, object? state) => ((Func<Message, T>)state!)(message),
            readBody);
        return await call.WaitAsync(DefaultTimeout, cancellationToken).ConfigureAwait(false);
    }

    private MessageBuffer CreateCallMessage(string member, string? signature)
    {
        using MessageWriter writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: DaemonContract.ServiceName,
            path: DaemonContract.ObjectPath,
            @interface: DaemonContract.InterfaceName,
            member: member,
            signature: signature);
        return writer.CreateMessage();
    }

    private MessageBuffer CreateMutationCall(string operation)
    {
        using MessageWriter writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: DaemonContract.ServiceName,
            path: DaemonContract.ObjectPath,
            @interface: DaemonContract.InterfaceName,
            member: "RequestMutation",
            signature: "s");
        writer.WriteString(operation);
        return writer.CreateMessage();
    }

    private async Task<T> CallWithStringAsync<T>(
        string member,
        string argument,
        Func<Message, T> readBody,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        MessageBuffer request = CreateStringCall(member, argument);
        Task<T> call = _connection.CallMethodAsync(
            request,
            static (Message message, object? state) => ((Func<Message, T>)state!)(message),
            readBody);
        return await call.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private MessageBuffer CreateStringCall(string member, string argument)
    {
        using MessageWriter writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: DaemonContract.ServiceName,
            path: DaemonContract.ObjectPath,
            @interface: DaemonContract.InterfaceName,
            member: member,
            signature: "s");
        writer.WriteString(argument);
        return writer.CreateMessage();
    }
}
