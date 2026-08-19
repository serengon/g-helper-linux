using GHelper.Daemon.Contract;
using GHelper.Daemon.Core;
using Tmds.DBus.Protocol;

namespace GHelper.Daemon.DBus;

public interface IPolkitTransport
{
    Task<bool> CheckAuthorizationAsync(
        CallerIdentity caller,
        string polkitAction,
        bool allowUserInteraction,
        string cancellationId);

    Task CancelCheckAuthorizationAsync(string cancellationId);
    void Abort();
}

public sealed class PolkitDbusTransport : IPolkitTransport, IDisposable
{
    private const string Service = "org.freedesktop.PolicyKit1";
    private const string Path = "/org/freedesktop/PolicyKit1/Authority";
    private const string Interface = "org.freedesktop.PolicyKit1.Authority";
    private readonly DBusConnection _connection;
    private int _aborted;

    public PolkitDbusTransport(DBusConnection connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public Task<bool> CheckAuthorizationAsync(
        CallerIdentity caller,
        string polkitAction,
        bool allowUserInteraction,
        string cancellationId)
    {
        MessageBuffer request = CreateCheckRequest(
            caller.UniqueBusName, polkitAction, allowUserInteraction, cancellationId);
        return _connection.CallMethodAsync(
            request,
            static (Message message, object? _) =>
            {
                var reader = message.GetBodyReader();
                reader.AlignStruct();
                bool authorized = reader.ReadBool();
                _ = reader.ReadBool(); // challenge
                var resultDetails = reader.ReadDictionaryStart();
                reader.SkipTo(resultDetails);
                return authorized;
            },
            null);
    }

    public Task CancelCheckAuthorizationAsync(string cancellationId)
    {
        using MessageWriter writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: Service,
            path: Path,
            @interface: Interface,
            member: "CancelCheckAuthorization",
            signature: "s");
        writer.WriteString(cancellationId);
        return _connection.CallMethodAsync(writer.CreateMessage());
    }

    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) == 0)
            _connection.Dispose();
    }

    public void Dispose() => Abort();

    private MessageBuffer CreateCheckRequest(
        string uniqueBusName,
        string polkitAction,
        bool allowUserInteraction,
        string cancellationId)
    {
        using MessageWriter writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: Service,
            path: Path,
            @interface: Interface,
            member: "CheckAuthorization",
            signature: "(sa{sv})sa{ss}us");
        writer.WriteStructureStart();
        writer.WriteString("system-bus-name");
        var subjectDetails = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("name");
        writer.WriteVariantString(uniqueBusName);
        writer.WriteDictionaryEnd(subjectDetails);
        writer.WriteString(polkitAction);
        var details = writer.WriteDictionaryStart();
        writer.WriteDictionaryEnd(details);
        writer.WriteUInt32(allowUserInteraction ? 1u : 0u);
        writer.WriteString(cancellationId);
        return writer.CreateMessage();
    }
}

public sealed class PolkitAuthorizationService : IAuthorizationService
{
    public static readonly TimeSpan InteractiveAuthorizationTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan CancellationTransportTimeout = TimeSpan.FromSeconds(2);
    private const int MaximumInFlightChecks = 2;
    private readonly IPolkitTransport _transport;
    private readonly BoundedCallRunner _checks;
    private readonly BoundedCallRunner _cancellations;
    private readonly Func<string> _cancellationIdFactory;
    private readonly TimeSpan _interactiveTimeout;
    private readonly TimeSpan _cancellationTimeout;

    public PolkitAuthorizationService(DBusConnection connection)
        : this(new PolkitDbusTransport(connection))
    { }

    public PolkitAuthorizationService(
        IPolkitTransport transport,
        Func<string>? cancellationIdFactory = null,
        int maximumInFlight = MaximumInFlightChecks,
        TimeSpan? interactiveTimeout = null,
        TimeSpan? cancellationTimeout = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _cancellationIdFactory = cancellationIdFactory ??
            (() => $"ghelperd-{Guid.NewGuid():N}");
        _checks = new BoundedCallRunner(maximumInFlight);
        _cancellations = new BoundedCallRunner(maximumInFlight);
        _interactiveTimeout = interactiveTimeout ?? InteractiveAuthorizationTimeout;
        _cancellationTimeout = cancellationTimeout ?? CancellationTransportTimeout;
        if (_interactiveTimeout <= TimeSpan.Zero ||
            _interactiveTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(interactiveTimeout));
        if (_cancellationTimeout <= TimeSpan.Zero ||
            _cancellationTimeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(cancellationTimeout));
    }

    public async ValueTask<bool> IsAuthorizedAsync(
        CallerIdentity caller,
        string polkitAction,
        bool allowUserInteraction,
        CancellationToken cancellationToken)
    {
        if (!DaemonContract.IsUniqueBusName(caller.UniqueBusName) ||
            !DaemonContract.Mutations.Any(m =>
                string.Equals(m.PolkitAction, polkitAction, StringComparison.Ordinal)))
            throw new DaemonRequestException(
                DaemonContract.ErrorInvalidArguments,
                "Invalid polkit authorization subject or action.");

        string cancellationId = _cancellationIdFactory();
        if (!IsValidCancellationId(cancellationId))
            throw new InvalidOperationException("Invalid internal polkit cancellation id.");

        using var interactiveTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        interactiveTimeout.CancelAfter(_interactiveTimeout);
        bool checkStarted = false;
        try
        {
            return await _checks.RunAsync(
                () =>
                {
                    checkStarted = true;
                    return _transport.CheckAuthorizationAsync(
                        caller, polkitAction, allowUserInteraction, cancellationId);
                },
                interactiveTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (checkStarted)
                await CancelAsync(cancellationId).ConfigureAwait(false);
            throw;
        }
        catch (DaemonRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ghelperd polkit check failed ({ex.GetType().Name}).");
            throw new DaemonRequestException(
                DaemonContract.ErrorFailed, "Polkit transport failed.");
        }
    }

    public static bool IsValidCancellationId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 ||
            !value.StartsWith("ghelperd-", StringComparison.Ordinal))
            return false;
        for (int i = "ghelperd-".Length; i < value.Length; i++)
        {
            char c = value[i];
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'))
                return false;
        }
        return value.Length > "ghelperd-".Length;
    }

    private async Task CancelAsync(string cancellationId)
    {
        using var transportTimeout =
            new CancellationTokenSource(_cancellationTimeout);
        try
        {
            await _cancellations.RunAsync(
                () => _transport.CancelCheckAuthorizationAsync(cancellationId),
                transportTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Tmds 0.94.1 cannot cancel an individual pending call. This
            // transport uses a dedicated connection, so abort it if the short
            // cancellation RPC cannot be confirmed. Pending tasks stay bounded
            // and are observed by BoundedCallRunner.
            Console.Error.WriteLine($"ghelperd polkit cancellation failed ({ex.GetType().Name}); aborting auth transport.");
            _transport.Abort();
        }
    }
}
