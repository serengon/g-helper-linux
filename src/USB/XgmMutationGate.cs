namespace GHelper.Linux.USB;

/// <summary>
/// Last-line guard immediately in front of XG Mobile HID transport writes.
/// It is independent of HidSharp so smoke/tests can exercise it without
/// enumerating or opening a real device.
/// </summary>
public static class XgmMutationGate
{
    public static bool Allow(string operation)
        => Helpers.RuntimeMode.TryAllowMutation($"XG Mobile HID {operation}");
}
