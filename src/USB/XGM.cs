using System.Text;
using HidSharp;

namespace GHelper.Linux.USB;

/// <summary>
/// XG Mobile dock HID-side controller. Mirrors the Windows g-helper
/// <c>app/USB/XGM.cs</c> protocol byte-for-byte.
///
/// <para>
/// The XG Mobile dock exposes a USB-HID interface (alongside the dGPU's
/// PCIe interface) that accepts feature reports on report id <c>0x5E</c>.
/// Through that channel we can:
/// <list type="bullet">
///   <item>Send the auth handshake the dock expects after the laptop
///         enables it (<see cref="Init"/>).</item>
///   <item>Toggle the front-panel LED ring and set its brightness
///         (<see cref="Light"/>, <see cref="LightBrightness"/>).</item>
///   <item>Send Aura-style mode/colour packets to the dock LEDs
///         (<see cref="LightMode"/>).</item>
///   <item>Reset or program the dock's GPU fan curve
///         (<see cref="Reset"/>, <see cref="SetFan"/>).</item>
/// </list>
/// </para>
///
/// <para>
/// Detection is independent of the laptop-side <c>egpu_connected</c>
/// firmware attribute - the dock might be physically present but the
/// kernel asus-armoury / asus-nb-wmi node may have a different opinion.
/// We trust the HID enumeration: VID 0x0B05 + one of the known dock PIDs
/// + a feature report >= 300 bytes.
/// </para>
///
/// <para>
/// All writes are wrapped in try/catch with logging - the dock's
/// firmware can hang the bus if it receives unexpected sequences, so we
/// fail soft rather than throw exceptions up to the UI.
/// </para>
/// </summary>
public static class XGM
{
    /// <summary>HID report id for all XG Mobile feature reports.</summary>
    public const byte XGM_REPORT_ID = 0x5E;

    private const int ASUS_ID = 0x0B05;

    /// <summary>The XG Mobile feature report length (matches Windows g-helper filter).</summary>
    public const int XGM_REPORT_LEN = 300;

    /// <summary>
    /// Known XG Mobile dock product IDs (USB VID 0x0B05).
    /// Different generations / SKUs:
    /// <list type="bullet">
    ///   <item>0x1970 - first-gen RTX 3070/3080 dock (Flow X13 GV301QE
    ///                  reference dock, this is what reporter of #86 has).</item>
    ///   <item>0x1A9A - second-gen RTX 4070/4080/4090 dock.</item>
    ///   <item>0x1C28 - sibling id of 0x1C29 on newer dock firmware.</item>
    ///   <item>0x1C29 - newer dock revision (Strix XG receptacle).</item>
    ///   <item>0x1BC1 - latest dock revision shipped with 2024+ Flow Z13.</item>
    /// </list>
    /// </summary>
    public static readonly int[] XGM_PIDS = { 0x1970, 0x1A9A, 0x1C28, 0x1C29, 0x1BC1 };

    /// <summary>
    /// Exact upstream device selection: the XG Mobile interface must be an
    /// ASUS device with a known product id, be openable, and expose a feature
    /// report of at least 300 bytes. Do not reuse the generic AURA discovery:
    /// an XG dock exposes sibling HID interfaces and the 0x5D AURA interface is
    /// not necessarily the 0x5E/300-byte XGM interface.
    /// </summary>
    public static HidDevice? GetDevice()
    {
        try
        {
            return DeviceList.Local.GetHidDevices(ASUS_ID).FirstOrDefault(device =>
                XGM_PIDS.Contains(device.ProductID)
                && device.CanOpen
                && device.GetMaxFeatureReportLength() >= XGM_REPORT_LEN);
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Error getting XGM device: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Whether an XG Mobile dock is currently visible on USB-HID. Independent
    /// of the laptop-side <c>egpu_connected</c> firmware bit.
    /// </summary>
    public static bool IsConnected()
    {
        return GetDevice() is not null;
    }

    // Kernel LED-class fallback. asus-wmi exposes the dock's ring as
    // "asus:xgm-<something>" with max_brightness=1, which needs no hidraw
    // access. It only does on/off - brightness and mode stay HID-only.

    private static string? _sysfsLedDir;
    private static bool _sysfsLedProbed;

    /// <summary>
    /// Path of the dock's LED-class directory, or null. The suffix varies by
    /// firmware, so match on the "asus:xgm" prefix rather than a fixed name.
    /// Cached: the node appears and disappears with the dock, so a miss is
    /// re-probed while a hit is kept only as long as it still exists.
    /// </summary>
    private static string? SysfsLedDir()
    {
        if (_sysfsLedProbed && _sysfsLedDir != null && Directory.Exists(_sysfsLedDir))
            return _sysfsLedDir;

        _sysfsLedProbed = true;
        _sysfsLedDir = null;
        try
        {
            if (!Directory.Exists(Platform.Linux.SysfsHelper.Leds))
                return null;

            foreach (var dir in Directory.EnumerateDirectories(Platform.Linux.SysfsHelper.Leds))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith("asus:xgm", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(dir, "brightness")))
                {
                    _sysfsLedDir = dir;
                    return dir;
                }
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"XGM.SysfsLedDir: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// True when the dock LED can be driven at all, over HID or the LED class.
    /// Use this for UI visibility; <see cref="IsConnected"/> stays HID-only
    /// because the brightness slider and mode packets need the HID channel.
    /// </summary>
    public static bool IsLightAvailable() => IsConnected() || SysfsLedDir() != null;

    /// <summary>
    /// Toggle the ring through the LED class. Returns false when the node is
    /// absent or not writable (it is root-owned without a udev rule).
    /// </summary>
    private static bool SysfsLight(bool on)
    {
        var dir = SysfsLedDir();
        if (dir == null)
            return false;

        var path = Path.Combine(dir, "brightness");
        bool ok = Platform.Linux.SysfsHelper.WriteInt(path, on ? 1 : 0);
        Helpers.Logger.WriteLine($"XGM.SysfsLight({on}) via {path}: {ok}");
        return ok;
    }

    /// <summary>
    /// Returns the first matching dock hidraw device path, or null. Useful for
    /// diagnostics and logging.
    /// </summary>
    public static string? GetDevicePath()
    {
        return GetDevice()?.DevicePath;
    }

    /// <summary>
    /// Send a feature report to all connected XG Mobile docks. Adds report id,
    /// pads to the dock's 300-byte report length, and uses the SetFeature
    /// ioctl path. Returns true if at least one dock accepted the write.
    /// All exceptions are swallowed and logged - the UI never throws up due
    /// to a chatty dock.
    /// </summary>
    public static bool Write(byte[] data, string? log = null)
    {
        if (!XgmMutationGate.Allow("write"))
            return false;

        try
        {
            HidDevice? device = GetDevice();
            if (device is null)
            {
                Helpers.Logger.WriteLine("XGM SUB device not found");
                return false;
            }

            using HidStream hidStream = device.Open();
            byte[] payload = new byte[XGM_REPORT_LEN];
            data.CopyTo(payload, 0);
            hidStream.SetFeature(payload);
            Helpers.Logger.WriteLine(
                $"XGM-{device.ProductID}|{device.GetMaxFeatureReportLength()}:" +
                BitConverter.ToString(data));
            return true;
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"XGM.Write: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Convenience: send several messages back-to-back to the dock.
    /// </summary>
    public static bool WriteMany(IEnumerable<byte[]> payloads, string? log = null)
    {
        bool any = false;
        foreach (var p in payloads)
        {
            if (Write(p, log))
                any = true;
        }
        return any;
    }

    /// <summary>
    /// Send the dock authentication handshake and restore the persisted
    /// LED on/off state. The dock expects to see the ASCII string
    /// <c>^ASUS Tech.Inc.</c> as the payload of a 0x5E feature report
    /// shortly after the laptop enables the PCIe link; without it,
    /// subsequent fan / LED writes are silently ignored on some firmware
    /// revisions. Mirrors Windows g-helper <c>USB/XGM.cs:Init</c> which
    /// also calls <c>Light(AppConfig.Is("xmg_light"))</c> right after
    /// the handshake so callers in the toggle-on path do not have to
    /// remember to push the light state separately.
    /// </summary>
    public static bool Init()
    {
        if (!IsConnected())
            return false;

        // Upstream sends the buffer verbatim. The leading '^' is byte 0x5E,
        // therefore it is already the HID report id and must not be prepended.
        var auth = Encoding.ASCII.GetBytes("^ASUS Tech.Inc.");
        Helpers.Logger.WriteLine("XGM: sending init handshake (^ASUS Tech.Inc.)");
        bool ok = Write(auth, "XGM:Init");

        // Exact current upstream wake packet (seerge/g-helper 682f87f2).
        Write(new byte[] { XGM_REPORT_ID, 0xE4, 0x02 }, "XGM:Init:E4:02");

        // Match Windows: restore the LED on/off state immediately after the
        // handshake. Brightness is restored separately by InitLight() at
        // startup. Failures are logged inside Light() and don't prevent
        // the rest of the toggle flow from continuing.
        try
        {
            bool on = Helpers.AppConfig.Get("xmg_light", 1) == 1;
            Light(on);
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"XGM.Init: post-handshake Light() failed: {ex.Message}");
        }

        return ok;
    }

    /// <summary>
    /// Toggle the dock's LED ring on or off. Two feature reports are sent in
    /// sequence: <c>[5E C5 0x50/0x00]</c> selects the lighting profile and
    /// <c>[5E BD 00 0x01/0x00]</c> commits the on/off state. Mirrors Windows
    /// g-helper <c>USB/XGM.cs:Light</c>.
    /// </summary>
    public static bool Light(bool on)
    {
        if (IsConnected())
        {
            bool ok1 = Write(new byte[] { XGM_REPORT_ID, 0xC5, on ? (byte)0x50 : (byte)0x00 }, "XGM:Light:profile");
            bool ok2 = Write(new byte[] { XGM_REPORT_ID, 0xBD, 0x00, on ? (byte)0x01 : (byte)0x00 }, "XGM:Light:onoff");
            Helpers.Logger.WriteLine($"XGM.Light({on}): profile={ok1} onoff={ok2}");
            if (ok1 && ok2)
                return true;
        }

        return SysfsLight(on);
    }

    /// <summary>
    /// Set LED ring brightness. Valid range 0..255 in protocol terms; the UI
    /// clamps to 0..3 (matches Aura keyboard brightness steps). Mirrors
    /// Windows g-helper <c>USB/XGM.cs:LightBrightness</c>.
    /// </summary>
    public static bool LightBrightness(byte level)
    {
        if (!IsConnected())
            return false;

        return Write(new byte[] { XGM_REPORT_ID, 0xBA, 0xC5, 0xC4, level }, "XGM:LightBrightness");
    }

    /// <summary>
    /// Send an Aura mode/colour packet to the dock LED ring. The caller
    /// passes a normal AURA-protocol packet (report id 0x5D); we copy it,
    /// rewrite the report id byte to 0x5E, and follow up with the
    /// <c>[5E B4]</c> + <c>[5E B5]</c> commit pair. Mirrors Windows
    /// g-helper <c>USB/XGM.cs:LightMode</c>.
    /// </summary>
    public static bool LightMode(byte[] auraPacket)
    {
        if (!IsConnected() || auraPacket == null || auraPacket.Length < 2)
            return false;

        // Copy and rewrite report id byte (auraPacket[0] == 0x5D for AURA).
        var copy = new byte[auraPacket.Length];
        Array.Copy(auraPacket, copy, auraPacket.Length);
        copy[0] = XGM_REPORT_ID;

        bool ok1 = Write(copy, "XGM:LightMode:packet");
        bool ok2 = Write(new byte[] { XGM_REPORT_ID, 0xB4 }, "XGM:LightMode:commit1");
        bool ok3 = Write(new byte[] { XGM_REPORT_ID, 0xB5 }, "XGM:LightMode:commit2");
        return ok1 && ok2 && ok3;
    }

    /// <summary>
    /// Restore the persisted LED state on dock connect / app startup. Reads
    /// <c>xmg_light</c> (on/off) and <c>xmg_brightness</c> (0..3) from
    /// AppConfig and pushes both to the dock.
    /// </summary>
    public static bool InitLight()
    {
        if (!IsLightAvailable())
            return false;

        bool on = Helpers.AppConfig.Get("xmg_light", 1) == 1;
        int brightness = Helpers.AppConfig.Get("xmg_brightness", 3);
        if (brightness < 0)
            brightness = 0;
        if (brightness > 3)
            brightness = 3;

        bool a = Light(on);
        if (!IsConnected())
            return a;

        bool b = LightBrightness((byte)brightness);
        return a && b;
    }

    /// <summary>
    /// Restore the dock fan curve to the firmware default. Sent before
    /// disabling the dock so the dock doesn't try to spin up a fan against a
    /// powered-down GPU. Mirrors Windows g-helper <c>USB/XGM.cs:Reset</c>.
    /// </summary>
    public static bool Reset()
    {
        if (!XgmMutationGate.Allow("reset"))
            return false;

        if (!IsConnected())
            return false;

        Helpers.Logger.WriteLine("XGM.Reset: restoring default fan curve");
        return Write(new byte[] { XGM_REPORT_ID, 0xD1, 0x02 }, "XGM:Reset");
    }

    /// <summary>
    /// Push a custom dock fan curve. The 16-byte argument encodes 8 (temp,
    /// pwm) pairs as two parallel arrays: bytes [0..7] are the eight
    /// temperatures (degrees C) and bytes [8..15] the eight PWM levels
    /// (percent, hard-capped to 72 by the UI). Mirrors Windows g-helper
    /// <c>USB/XGM.cs:SetFan</c>:
    /// <code>Write([XGM_REPORT_ID, 0xD1, 0x01, ..curve])</code>
    /// On the wire: <c>5E D1 01 &lt;curve16&gt;</c> = 19 bytes content.
    /// </summary>
    public static bool SetFan(byte[] curve16)
    {
        if (!IsConnected())
            return false;

        if (curve16 == null || curve16.Length != 16)
        {
            Helpers.Logger.WriteLine($"XGM.SetFan: bad curve length {curve16?.Length ?? 0} - expected 16");
            return false;
        }

        var packet = new byte[3 + curve16.Length];
        packet[0] = XGM_REPORT_ID;
        packet[1] = 0xD1;
        packet[2] = 0x01;
        Array.Copy(curve16, 0, packet, 3, curve16.Length);
        return Write(packet, "XGM:SetFan");
    }

    /// <summary>Exact upstream dock shutdown notification.</summary>
    public static bool NotifyShutdown()
    {
        return IsConnected()
            && Write(new byte[] { XGM_REPORT_ID, 0xE4, 0x01 }, "XGM:Shutdown");
    }

    /// <summary>
    /// One-shot startup hook called from <c>App</c> after the AURA path has
    /// finished. If a dock is present we send the auth handshake and restore
    /// the persisted LED state. Failures are logged and swallowed.
    /// </summary>
    public static void InitHardware()
    {
        try
        {
            if (!IsConnected())
            {
                Helpers.Logger.WriteLine("XGM: no dock present, skipping init");
                return;
            }

            Helpers.Logger.WriteLine($"XGM: dock detected at {GetDevicePath()} - initializing");
            Init();
            InitLight();

            // If the dock is enabled at startup, the dGPU is already on the
            // PCIe bus - probe for the RX 6850M XT and remember it. See
            // Gpu/AMD/LinuxAmdDgpuDetect.RefreshXgmSpecialFlag for details.
            try
            { Gpu.AMD.LinuxAmdDgpuDetect.RefreshXgmSpecialFlag(); }
            catch (Exception ex) { Helpers.Logger.WriteLine($"XGM RX6850M probe: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"XGM.InitHardware: {ex.Message}");
        }
    }
}
