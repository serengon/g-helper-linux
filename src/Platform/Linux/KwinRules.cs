using System.Text;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Installs a KWin window rule that stops the on-screen keyboard window from
/// taking keyboard focus when tapped. Without it, clicking a key would focus
/// the keyboard and the injected keystrokes would land in the keyboard window
/// instead of the app being typed into.
///
/// KWin window rules work on both X11 and Wayland Plasma sessions, which
/// covers SteamOS desktop mode. On non-KDE desktops this is a no-op; there
/// the OSK relies on the target keeping focus (works on most X11 WMs via
/// the window's no-activate hints, not guaranteed on GNOME Wayland).
///
/// The rule matches wmclass "ghelper" plus the exact keyboard window title,
/// so the main G-Helper windows keep normal focus behavior.
/// </summary>
internal static class KwinRules
{
    private const string GroupName = "ghelper-osk";

    /// <summary>Idempotently append our rule group to kwinrulesrc and tell
    /// KWin to reload. Safe to call on every OSK open.</summary>
    internal static void EnsureOskRule(string windowTitle)
    {
        try
        {
            var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
            if (!desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase))
                return;

            string config = AbsoluteUserPaths.ConfigRoot();
            string path = Path.Combine(config, "kwinrulesrc");

            string text = File.Exists(path) ? File.ReadAllText(path) : "";
            if (text.Contains($"[{GroupName}]"))
                return;

            var sb = new StringBuilder(text);
            if (text.Length > 0 && !text.EndsWith('\n'))
                sb.Append('\n');

            sb.AppendLine();
            sb.AppendLine($"[{GroupName}]");
            sb.AppendLine("Description=G-Helper on-screen keyboard (no focus steal)");
            sb.AppendLine("acceptfocus=false");
            sb.AppendLine("acceptfocusrule=2");
            sb.AppendLine("above=true");
            sb.AppendLine("aboverule=2");
            sb.AppendLine("skiptaskbar=true");
            sb.AppendLine("skiptaskbarrule=2");
            sb.AppendLine("skippager=true");
            sb.AppendLine("skippagerrule=2");
            sb.AppendLine("skipswitcher=true");
            sb.AppendLine("skipswitcherrule=2");
            sb.AppendLine($"title={windowTitle}");
            sb.AppendLine("titlematch=1");
            sb.AppendLine("wmclass=ghelper");
            sb.AppendLine("wmclassmatch=1");

            string updated = AddToRuleList(sb.ToString());
            Directory.CreateDirectory(config);
            File.WriteAllText(path, updated);
            Logger.WriteLine($"KwinRules: appended {GroupName} rule to {path}");

            Reconfigure();
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"KwinRules: ensure rule failed: {ex.Message}");
        }
    }

    /// <summary>Register the group in [General]. Modern kwinrulesrc lists
    /// group names in "rules="; the pre-5.20 format counts numeric groups
    /// via "count=", which KWin only consults when "rules=" is absent, so
    /// adding "rules=" including the legacy numeric names migrates both.</summary>
    private static string AddToRuleList(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        int generalIdx = lines.FindIndex(l => l.Trim() == "[General]");

        if (generalIdx < 0)
        {
            lines.InsertRange(0, ["[General]", $"rules={GroupName}", ""]);
            return string.Join('\n', lines);
        }

        int sectionEnd = lines.Count;
        int rulesIdx = -1;
        int count = 0;
        for (int i = generalIdx + 1; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('['))
            {
                sectionEnd = i;
                break;
            }
            if (t.StartsWith("rules=", StringComparison.OrdinalIgnoreCase))
                rulesIdx = i;
            else if (t.StartsWith("count=", StringComparison.OrdinalIgnoreCase))
                int.TryParse(t["count=".Length..], out count);
        }

        if (rulesIdx >= 0)
        {
            var value = lines[rulesIdx][(lines[rulesIdx].IndexOf('=') + 1)..];
            var names = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (!names.Contains(GroupName))
                names.Add(GroupName);
            lines[rulesIdx] = "rules=" + string.Join(',', names);
        }
        else
        {
            // Legacy numeric-only file: emit rules= naming groups 1..count
            // plus ours, which preserves the old rules under the new key.
            var names = Enumerable.Range(1, count).Select(n => n.ToString()).Append(GroupName);
            lines.Insert(sectionEnd, "rules=" + string.Join(',', names));
        }
        return string.Join('\n', lines);
    }

    private static void Reconfigure()
    {
        var result = SysfsHelper.RunCommand("busctl",
            "--user call org.kde.KWin /KWin org.kde.KWin reconfigure");
        if (result == null)
            SysfsHelper.RunCommand("dbus-send",
                "--session --type=method_call --dest=org.kde.KWin /KWin org.kde.KWin.reconfigure");
    }
}
