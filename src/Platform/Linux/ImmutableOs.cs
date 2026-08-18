namespace GHelper.Linux.Platform.Linux;

// Immutable OS detection (Fedora Atomic, Silverblue, Kinoite, Bazzite,
// Universal Blue, openSUSE MicroOS, SteamOS). /usr/share is read-only on
// these systems so desktop entries and icons go to ~/.local/share/ instead.
public static class ImmutableOs
{
    private static bool? _detected;
    private static bool? _steamOs;

    // True on systems where /usr is read-only: OSTree-based distros and
    // SteamOS (A/B image with steamos-readonly).
    public static bool IsImmutable => _detected ??=
        !NixOS.IsNixOS && (File.Exists("/run/ostree-booted") || IsSteamOs);

    // SteamOS 3.x (Steam Deck, Legion Go S factory image and installs).
    public static bool IsSteamOs => _steamOs ??= DetectSteamOs();

    private static bool DetectSteamOs()
    {
        try
        {
            if (File.Exists("/etc/os-release"))
                foreach (var line in File.ReadLines("/etc/os-release"))
                    if (line.StartsWith("ID=") && line[3..].Trim('"') == "steamos")
                        return true;
        }
        catch { }
        return false;
    }

    // True when the root filesystem is mounted read-only right now (stock
    // SteamOS with steamos-readonly enabled). /etc stays writable there (an
    // overlay), but anything on / itself (/opt, /usr/local) is sealed.
    public static bool IsRootReadOnly()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var parts = line.Split(' ');
                if (parts.Length >= 4 && parts[1] == "/")
                    return parts[3].Split(',').Contains("ro");
            }
        }
        catch { }
        return false;
    }

    // ~/.local/share/applications/ghelper.desktop
    public static string UserDesktopPath()
    {
        return Helpers.AbsoluteUserPaths.DataPath(
            "applications", "ghelper.desktop");
    }

    // ~/.local/share/icons/hicolor/256x256/apps/ghelper.png
    public static string UserIconPath()
    {
        return Helpers.AbsoluteUserPaths.DataPath(
            "icons", "hicolor", "256x256", "apps", "ghelper.png");
    }
}
