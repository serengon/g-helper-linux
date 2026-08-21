using System.Text;

namespace GHelper.Daemon.Contract;

public static class DaemonContract
{
    public const uint ApiVersion = 1;
    public const string DaemonVersion = "1.0.90-x13.2-xg-mvp1";
    public const string ServiceName = "org.ghelper.Daemon1";
    public const string ObjectPath = "/org/ghelper/Daemon1";
    public const string InterfaceName = "org.ghelper.Daemon1";

    public const string ErrorInvalidArguments = InterfaceName + ".Error.InvalidArguments";
    public const string ErrorCallerUnknown = InterfaceName + ".Error.CallerUnknown";
    public const string ErrorNotAuthorized = InterfaceName + ".Error.NotAuthorized";
    public const string ErrorUnsupportedOperation = InterfaceName + ".Error.UnsupportedOperation";
    public const string ErrorTimedOut = InterfaceName + ".Error.TimedOut";
    public const string ErrorCancelled = InterfaceName + ".Error.Cancelled";
    public const string ErrorBusy = InterfaceName + ".Error.Busy";
    public const string ErrorFailed = InterfaceName + ".Error.Failed";

    public const string MessageInvalidArguments = "The request arguments are invalid.";
    public const string MessageCallerUnknown = "The caller identity could not be established.";
    public const string MessageNotAuthorized = "Authorization was denied.";
    public const string MessageUnsupportedOperation = "Hardware mutations are unavailable.";
    public const string MessageTimedOut = "The request timed out.";
    public const string MessageCancelled = "The request was cancelled.";
    public const string MessageBusy = "The daemon is busy; retry later.";
    public const string MessageFailed = "The daemon rejected the request.";

    public const string StatusState = "ready-x13-gpu-live";
    public const string StatusDetail = "live XG Mobile and internal dGPU Eco/Standard transitions are available; other hardware mutations remain disabled";

    public static readonly string[] Capabilities =
    [
        "read.version",
        "read.capabilities",
        "read.status",
        "mutate.xg-mode",
        "mutate.dgpu-mode"
    ];

    public static readonly MutationDefinition[] Mutations =
    [
        new("set-platform-profile", "org.ghelper.daemon.set-platform-profile"),
        new("set-charge-limit", "org.ghelper.daemon.set-charge-limit"),
        new("set-fan-curve", "org.ghelper.daemon.set-fan-curve"),
        new("set-gpu-mode", "org.ghelper.daemon.set-gpu-mode"),
        new("enable-dgpu-mode", "org.ghelper.daemon.set-gpu-mode"),
        new("disable-dgpu-mode", "org.ghelper.daemon.set-gpu-mode"),
        new("enable-xg-mode", "org.ghelper.daemon.set-xg-mode"),
        new("disable-xg-mode", "org.ghelper.daemon.set-xg-mode")
    ];

    public const string IntrospectionXml = """
        <interface name="org.ghelper.Daemon1">
          <method name="GetVersion">
            <arg name="api_version" type="u" direction="out"/>
            <arg name="daemon_version" type="s" direction="out"/>
          </method>
          <method name="GetCapabilities">
            <arg name="capabilities" type="as" direction="out"/>
          </method>
          <method name="GetStatus">
            <arg name="state" type="s" direction="out"/>
            <arg name="detail" type="s" direction="out"/>
          </method>
          <method name="RequestMutation">
            <arg name="operation" type="s" direction="in"/>
          </method>
        </interface>
        """;

    public static readonly ReadOnlyMemory<byte> IntrospectionBytes =
        Encoding.UTF8.GetBytes(IntrospectionXml);

    public static bool TryGetMutation(string operation, out MutationDefinition definition)
    {
        definition = default;
        if (!IsValidOperationName(operation))
            return false;

        foreach (MutationDefinition candidate in Mutations)
        {
            if (string.Equals(candidate.Operation, operation, StringComparison.Ordinal))
            {
                definition = candidate;
                return true;
            }
        }
        return false;
    }

    public static bool IsValidOperationName(string? operation)
    {
        if (string.IsNullOrEmpty(operation) || operation.Length > 64)
            return false;
        foreach (char c in operation)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'))
                return false;
        }
        return true;
    }

    public static bool IsUniqueBusName(string? sender)
    {
        if (string.IsNullOrEmpty(sender) || sender.Length > 255 || sender[0] != ':')
            return false;
        bool segmentHasDigit = false;
        for (int i = 1; i < sender.Length; i++)
        {
            char c = sender[i];
            if (c == '.')
            {
                if (!segmentHasDigit)
                    return false;
                segmentHasDigit = false;
            }
            else if (c >= '0' && c <= '9')
            {
                segmentHasDigit = true;
            }
            else
            {
                return false;
            }
        }
        return segmentHasDigit;
    }

    public static string GetPublicErrorMessage(string errorName) => errorName switch
    {
        ErrorInvalidArguments => MessageInvalidArguments,
        ErrorCallerUnknown => MessageCallerUnknown,
        ErrorNotAuthorized => MessageNotAuthorized,
        ErrorUnsupportedOperation => MessageUnsupportedOperation,
        ErrorTimedOut => MessageTimedOut,
        ErrorCancelled => MessageCancelled,
        ErrorBusy => MessageBusy,
        _ => MessageFailed
    };
}

public readonly record struct MutationDefinition(string Operation, string PolkitAction);
public readonly record struct DaemonVersionInfo(uint ApiVersion, string DaemonVersion);
public readonly record struct DaemonStatus(string State, string Detail);
