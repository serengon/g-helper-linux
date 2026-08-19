using GHelper.Daemon.Contract;
using GHelper.Daemon.Core;
using Tmds.DBus.Protocol;

namespace GHelper.Daemon.DBus;

public interface ICallerIdentityTransport
{
    Task<uint> QueryUInt32Async(string member, string uniqueBusName);
}

public sealed class DbusCallerIdentityTransport : ICallerIdentityTransport
{
    private const string BusService = "org.freedesktop.DBus";
    private const string BusPath = "/org/freedesktop/DBus";
    private const string BusInterface = "org.freedesktop.DBus";
    private readonly DBusConnection _connection;

    public DbusCallerIdentityTransport(DBusConnection connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public Task<uint> QueryUInt32Async(string member, string uniqueBusName)
    {
        using MessageWriter writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: BusService,
            path: BusPath,
            @interface: BusInterface,
            member: member,
            signature: "s");
        writer.WriteString(uniqueBusName);
        return _connection.CallMethodAsync(
            writer.CreateMessage(),
            static (Message message, object? _) => message.GetBodyReader().ReadUInt32(),
            null);
    }
}

public sealed class DbusCallerIdentityResolver : ICallerIdentityResolver
{
    public static readonly TimeSpan TransportTimeout = TimeSpan.FromSeconds(2);
    private readonly ICallerIdentityTransport _transport;
    private readonly BoundedCallRunner _calls;
    private readonly TimeSpan _transportTimeout;

    public DbusCallerIdentityResolver(DBusConnection connection)
        : this(new DbusCallerIdentityTransport(connection))
    { }

    public DbusCallerIdentityResolver(
        ICallerIdentityTransport transport,
        int maximumInFlight = 2,
        TimeSpan? transportTimeout = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _calls = new BoundedCallRunner(maximumInFlight);
        _transportTimeout = transportTimeout ?? TransportTimeout;
        if (_transportTimeout <= TimeSpan.Zero || _transportTimeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(transportTimeout));
    }

    public async ValueTask<CallerIdentity> ResolveAsync(
        string uniqueBusName,
        CancellationToken cancellationToken)
    {
        if (!DaemonContract.IsUniqueBusName(uniqueBusName))
            throw new DaemonRequestException(
                DaemonContract.ErrorCallerUnknown, "Invalid D-Bus sender identity.");

        using var transportTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        transportTimeout.CancelAfter(_transportTimeout);
        try
        {
            // Sequential queries bound a single identity request to one
            // underlying call at a time. The runner retains a slot after a
            // timeout until Tmds completes and the result is observed.
            uint uid = await _calls.RunAsync(
                () => _transport.QueryUInt32Async("GetConnectionUnixUser", uniqueBusName),
                transportTimeout.Token).ConfigureAwait(false);
            uint pid = await _calls.RunAsync(
                () => _transport.QueryUInt32Async("GetConnectionUnixProcessID", uniqueBusName),
                transportTimeout.Token).ConfigureAwait(false);
            return new CallerIdentity(uniqueBusName, uid, pid);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DaemonRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ghelperd caller lookup failed ({ex.GetType().Name}).");
            throw new DaemonRequestException(
                DaemonContract.ErrorCallerUnknown,
                "The D-Bus daemon could not identify the caller.");
        }
    }
}
