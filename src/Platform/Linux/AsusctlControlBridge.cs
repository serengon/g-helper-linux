using System.Diagnostics;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Bridge to the already-installed asusd service. This keeps
/// the desktop process unprivileged while allowing the same hardware controls
/// exposed by asusctl (profiles, battery, fan curves and firmware attributes).
/// </summary>
public static class AsusctlControlBridge
{
    private const string AsusctlPath = "/usr/bin/asusctl";
    private const string BusctlPath = "/usr/bin/busctl";
    private const string AsusdService = "xyz.ljones.Asusd";
    private const string ArmouryInterface = "xyz.ljones.AsusArmoury";
    private const int CommandTimeoutMs = 5000;

    public static CommandResult SetPerformanceProfile(int mode)
    {
        if (!RuntimeMode.IsPocFunctional)
            return CommandResult.Refused;

        string? profile = ProfileToken(mode);
        return profile == null
            ? new(false, "Unsupported profile.")
            : RunAsusctl("profile", "set", profile);
    }

    public static CommandResult SetBatteryLimit(int percent)
    {
        if (!RuntimeMode.IsPocFunctional)
            return CommandResult.Refused;

        percent = Math.Clamp(percent, 40, 100);
        if (AppConfig.IsChargeLimit6080())
            percent = percent < 70 ? 60 : percent < 90 ? 80 : 100;

        return RunAsusctl(
            "battery", "limit",
            percent.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static CommandResult SetArmouryAttribute(string property, int value)
        => SetArmouryAttribute(property,
            value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static CommandResult SetArmouryAttribute(string property, string value)
    {
        if (!RuntimeMode.IsPocFunctional)
            return CommandResult.Refused;

        // asusctl 6.4 warns about multiple interfaces on machines that expose
        // both Platform and per-attribute Armoury objects. Address the exact
        // object directly so a successful CLI exit can never refer to the
        // wrong interface. Keep asusctl only as a compatibility fallback.
        if (File.Exists(BusctlPath)
            && int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int numericValue)
            && IsSafeArmouryProperty(property))
        {
            return RunBusctlArmourySet(property, numericValue);
        }
        return RunAsusctl("armoury", "set", property, value);
    }

    public static CommandResult SetKeyboardBrightness(int level)
    {
        if (!RuntimeMode.IsPocFunctional)
            return CommandResult.Refused;
        string token = Math.Clamp(level, 0, 3) switch
        {
            0 => "off",
            1 => "low",
            2 => "med",
            _ => "high",
        };
        return RunAsusctl("leds", "set", token);
    }

    public static CommandResult SetFanCurve(int profileMode, int fanIndex, byte[] curve)
    {
        if (!RuntimeMode.IsPocFunctional || curve.Length != 16)
            return CommandResult.Refused;
        string? profile = ProfileToken(profileMode);
        string? fan = FanToken(fanIndex);
        if (profile == null || fan == null)
            return new(false, "Unsupported profile or fan.");

        string data = string.Join(',', Enumerable.Range(0, 8).Select(i =>
            $"{curve[i]}c:{Math.Clamp(curve[8 + i], (byte)0, (byte)100)}%"));
        var write = RunAsusctl(
            "fan-curve", "--mod-profile", profile, "--fan", fan, "--data", data);
        if (!write.Success)
            return write;
        return RunAsusctl(
            "fan-curve", "--mod-profile", profile, "--fan", fan,
            "--enable-fan-curve", "true");
    }

    public static CommandResult SetFanCurveEnabled(int profileMode, int fanIndex, bool enabled)
    {
        if (!RuntimeMode.IsPocFunctional)
            return CommandResult.Refused;
        string? profile = ProfileToken(profileMode);
        string? fan = FanToken(fanIndex);
        return profile == null || fan == null
            ? new(false, "Unsupported profile or fan.")
            : RunAsusctl(
                "fan-curve", "--mod-profile", profile, "--fan", fan,
                "--enable-fan-curve", enabled ? "true" : "false");
    }

    public static CommandResult ResetFanCurves()
    {
        if (!RuntimeMode.IsPocFunctional)
            return CommandResult.Refused;
        return RunAsusctl("fan-curve", "--default");
    }

    /// <summary>
    /// Route root-owned sysfs writes to the matching asusd operation. Returns
    /// false only when the path is not one this bridge owns.
    /// </summary>
    public static bool TryWriteSysfs(string path, string value, out bool success)
    {
        success = false;
        if (!RuntimeMode.IsPocFunctional)
            return false;

        string file = Path.GetFileName(path);
        if (file == "charge_control_end_threshold")
        {
            success = int.TryParse(value, out int limit) && SetBatteryLimit(limit).Success;
            return true;
        }

        if (file == "brightness" && path.Contains("asus::kbd_backlight", StringComparison.Ordinal))
        {
            success = int.TryParse(value, out int level) && SetKeyboardBrightness(level).Success;
            return true;
        }

        string attribute = file == "current_value"
            ? Path.GetFileName(Path.GetDirectoryName(path)!)
            : file;
        if (attribute == AsusAttributes.ThrottleThermalPolicy.LegacyName)
        {
            success = int.TryParse(value, out int mode) && SetPerformanceProfile(mode).Success;
            return true;
        }

        string[] armouryAttributes =
        {
            "boot_sound", "charge_mode", "dgpu_disable", "egpu_enable",
            "ppt_pl1_spl", "ppt_pl2_sppt", "ppt_pl3_fppt",
            "ppt_apu_sppt", "ppt_platform_sppt", "nv_dynamic_boost",
            "nv_temp_target", "nv_base_tgp", "nv_tgp", "gpu_mux_mode",
            "panel_overdrive", "mini_led_mode", "screen_auto_brightness",
        };
        if (!armouryAttributes.Contains(attribute, StringComparer.Ordinal))
            return false;

        success = SetArmouryAttribute(attribute, value).Success;
        return true;
    }

    internal static string? ProfileToken(int mode) => mode switch
    {
        2 => "quiet",
        0 => "balanced",
        1 => "performance",
        _ => null,
    };

    internal static string? FanToken(int fanIndex) => fanIndex switch
    {
        0 => "cpu",
        1 => "gpu",
        2 => "mid",
        _ => null,
    };

    private static bool IsSafeArmouryProperty(string property)
        => property.Length is > 0 and <= 64
            && property.All(c => (c >= 'a' && c <= 'z') || c == '_' || char.IsAsciiDigit(c));

    private static CommandResult RunBusctlArmourySet(string property, int value)
    {
        string objectPath = $"/xyz/ljones/asus_armoury/{property}";
        string[] arguments =
        [
            "--system", "set-property", AsusdService, objectPath,
            ArmouryInterface, "CurrentValue", "i",
            value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ];
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = BusctlPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return new(false, "Could not start busctl.");
            if (!process.WaitForExit(CommandTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new(false, "asusd D-Bus write timed out.");
            }

            string stdout = process.StandardOutput.ReadToEnd().Trim();
            string stderr = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode != 0)
            {
                string detail = stderr.Length > 0 ? stderr : stdout;
                return new(false, detail.Length > 0 ? detail : $"busctl failed ({process.ExitCode}).");
            }

            Logger.WriteLine(
                $"{RuntimeMode.PocBanner}: exact asusd attribute applied ({property}={value})");
            return new(true, stdout);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Exact asusd attribute operation failed", ex);
            return new(false, "asusd D-Bus write failed.");
        }
    }

    private static CommandResult RunAsusctl(params string[] arguments)
    {
        if (!File.Exists(AsusctlPath))
            return new(false, "asusctl is not installed.");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = AsusctlPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return new(false, "Could not start asusctl.");

            if (!process.WaitForExit(CommandTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new(false, "asusctl timed out.");
            }

            string stdout = process.StandardOutput.ReadToEnd().Trim();
            string stderr = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode != 0)
            {
                string detail = stderr.Length > 0 ? stderr : stdout;
                return new(false, detail.Length > 0 ? detail : $"asusctl failed ({process.ExitCode}).");
            }

            Logger.WriteLine(
                $"{RuntimeMode.PocBanner}: asusd operation applied ({string.Join(' ', arguments)})");
            return new(true, stdout);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("POC asusctl operation failed", ex);
            return new(false, "asusctl operation failed.");
        }
    }
}

public readonly record struct CommandResult(bool Success, string Message)
{
    public static CommandResult Refused =>
        new(false, "Operation is unavailable in this mode.");
}
