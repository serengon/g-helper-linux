using GHelper.Daemon.Contract;
using GHelper.Daemon.Core;
using GHelper.Daemon.DBus;
using GHelper.Daemon.Hosting;
using GHelper.Daemon.Hardware;
using Tmds.DBus.Protocol;

namespace GHelper.Daemon;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 0)
        {
            Console.Error.WriteLine("ghelperd accepts no command-line operations.");
            return DaemonStartupContract.ExitUsage;
        }

        string address = DBusAddress.System
            ?? throw new InvalidOperationException("The system D-Bus address is unavailable.");
        using var serviceConnection = new DBusConnection(address);
        using var polkitConnection = new DBusConnection(address);
        using var polkitTransport = new PolkitDbusTransport(polkitConnection);
        using var startup = new CancellationTokenSource(DaemonStartupContract.StartupTimeout);
        bool nameAcquired = false;
        try
        {
            await serviceConnection.ConnectAsync().AsTask()
                .WaitAsync(startup.Token).ConfigureAwait(false);
            await polkitConnection.ConnectAsync().AsTask()
                .WaitAsync(startup.Token).ConfigureAwait(false);

            var identity = new DbusCallerIdentityResolver(serviceConnection);
            var authorization = new PolkitAuthorizationService(polkitTransport);
            var xgExecutor = new XgMobileMutationExecutor();
            var core = new DaemonCore(
                identity,
                authorization,
                mutationExecutionEnabled: true,
                mutationExecutor: xgExecutor);
            serviceConnection.AddMethodHandler(new DaemonMethodHandler(core));

            nameAcquired = await serviceConnection.TryRequestNameAsync(
                    DaemonContract.ServiceName,
                    DaemonStartupContract.NameRequestOptions)
                .WaitAsync(startup.Token).ConfigureAwait(false);
            if (!nameAcquired)
            {
                Console.Error.WriteLine("ghelperd service name is already owned; refusing to queue or replace it.");
                return DaemonStartupContract.ExitNameUnavailable;
            }

            // With RequestNameOptions.None the name cannot be replaced. Any
            // ownership loss therefore coincides with connection loss, which
            // is an explicit non-success termination below.
            Exception? disconnect = await serviceConnection.DisconnectedAsync().ConfigureAwait(false);
            if (disconnect is not null)
                Console.Error.WriteLine($"ghelperd service connection lost ({disconnect.GetType().Name}).");
            return DaemonStartupContract.ExitCodeFor(
                disconnect is null
                    ? DaemonTerminationReason.CleanDisconnect
                    : DaemonTerminationReason.ConnectionLost);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("ghelperd startup or shutdown timed out.");
            return DaemonStartupContract.ExitFailure;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ghelperd failed closed ({ex.GetType().Name}).");
            return DaemonStartupContract.ExitFailure;
        }
        finally
        {
            if (nameAcquired)
            {
                using var shutdown =
                    new CancellationTokenSource(DaemonStartupContract.ShutdownTimeout);
                try
                {
                    await serviceConnection.ReleaseNameAsync(DaemonContract.ServiceName)
                        .WaitAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"ghelperd name release incomplete ({ex.GetType().Name}).");
                }
            }
        }
    }
}
