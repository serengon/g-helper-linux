using System.Text;
using HidSharp;

namespace GHelper.Daemon.Hardware;

/// <summary>
/// Privileged Linux transport for the exact XG Mobile initialization frames
/// used by seerge/g-helper app/USB/XGM.cs at commit
/// 682f87f2c277049fb56232d31d7c95144e1cf73e.
/// </summary>
internal static class XgMobileHid
{
    private const byte ReportId = 0x5E;
    private const int AsusId = 0x0B05;
    private const int ReportLength = 300;
    private static readonly int[] DeviceIds = [0x1970, 0x1A9A, 0x1C28, 0x1C29, 0x1BC1];

    public static HidDevice? GetDevice()
    {
        return DeviceList.Local.GetHidDevices(AsusId).FirstOrDefault(device =>
            DeviceIds.Contains(device.ProductID)
            && device.CanOpen
            && device.GetMaxFeatureReportLength() >= ReportLength);
    }

    public static bool Initialize()
    {
        HidDevice? device = GetDevice();
        if (device is null)
        {
            Console.Error.WriteLine("XG Mobile HID interface (feature report >= 300) was not found.");
            return false;
        }

        Console.WriteLine(
            $"XG Mobile HID: {device.DevicePath}, pid=0x{device.ProductID:X4}, " +
            $"maxFeature={device.GetMaxFeatureReportLength()}.");

        // These are complete feature reports, including report id 0x5E.
        // '^' is ASCII 0x5E, exactly as in the upstream source.
        Write(device, Encoding.ASCII.GetBytes("^ASUS Tech.Inc."));
        Write(device, [ReportId, 0xE4, 0x02]);

        // Upstream Init() immediately restores the light state. The daemon
        // uses the upstream default (on); the user application reapplies the
        // persisted preference when the graphical session returns.
        Write(device, [ReportId, 0xC5, 0x50]);
        Write(device, [ReportId, 0xBD, 0x00, 0x01]);
        return true;
    }

    private static void Write(HidDevice device, byte[] data)
    {
        using HidStream stream = device.Open();
        byte[] payload = new byte[ReportLength];
        data.CopyTo(payload, 0);
        stream.SetFeature(payload);
        Console.WriteLine($"XG Mobile HID SetFeature: {BitConverter.ToString(data)}");
    }
}
