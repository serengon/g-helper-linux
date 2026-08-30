namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// NixOS detection retained for compatibility. The upstream Nix package path
/// is removed, so no executable or integration file is discovered or trusted.
/// </summary>
public static class NixOS
{
    private static bool? _detected;

    public static bool IsNixOS => _detected ??= File.Exists("/etc/NIXOS");

    public static string? IconFilePath() => null;
    public static string? ResolveGpuHelper() => null;
    public static string? ResolveGpuBlockHelper() => null;
    public static string? ResolveRyzenadj() => null;
    public static string? StableLauncherExec() => null;

    public static bool SkipSelfInstall => false;
    public static bool SkipUdevWarning => false;
    public static bool ManagedByModule => false;
}
