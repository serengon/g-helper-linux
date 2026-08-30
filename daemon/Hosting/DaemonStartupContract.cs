using Tmds.DBus.Protocol;

namespace GHelper.Daemon.Hosting;

public enum DaemonTerminationReason
{
    CleanDisconnect,
    ConnectionLost,
    NameUnavailable
}

public static class DaemonStartupContract
{
    public const int ExitSuccess = 0;
    public const int ExitFailure = 1;
    public const int ExitUsage = 64;
    public const int ExitNameUnavailable = 73;
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
    public const RequestNameOptions NameRequestOptions = RequestNameOptions.None;

    public static int ExitCodeFor(DaemonTerminationReason reason) => reason switch
    {
        DaemonTerminationReason.CleanDisconnect => ExitSuccess,
        DaemonTerminationReason.NameUnavailable => ExitNameUnavailable,
        _ => ExitFailure
    };
}
