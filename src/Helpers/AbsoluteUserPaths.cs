namespace GHelper.Linux.Helpers;

/// <summary>
/// Resolves user-owned paths without ever falling back to a relative path or
/// a shared predictable temporary directory. Invalid XDG values are ignored;
/// an absolute HOME or runtime user-profile path is then required.
/// </summary>
public static class AbsoluteUserPaths
{
    public static string HomeDirectory()
        => ResolveHome(
            Environment.GetEnvironmentVariable("HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string ConfigRoot()
        => ResolveConfigRoot(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            Environment.GetEnvironmentVariable("HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string CacheRoot()
        => ResolveCacheRoot(
            Environment.GetEnvironmentVariable("XDG_CACHE_HOME"),
            Environment.GetEnvironmentVariable("HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string DataRoot()
        => ResolveDataRoot(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
            Environment.GetEnvironmentVariable("HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string ConfigPath(params string[] components)
        => Combine(ConfigRoot(), components);

    public static string CachePath(params string[] components)
        => Combine(CacheRoot(), components);

    public static string DataPath(params string[] components)
        => Combine(DataRoot(), components);

    internal static string ResolveHome(string? home, string? userProfile)
    {
        if (IsAbsolute(home))
            return Path.GetFullPath(home!);
        if (IsAbsolute(userProfile))
            return Path.GetFullPath(userProfile!);
        throw new InvalidOperationException(
            "No absolute user home/profile path is available; refusing filesystem access.");
    }

    internal static string ResolveConfigRoot(
        string? xdgConfigHome, string? home, string? userProfile)
        => ResolveXdgRoot(xdgConfigHome, home, userProfile, ".config");

    internal static string ResolveCacheRoot(
        string? xdgCacheHome, string? home, string? userProfile)
        => ResolveXdgRoot(xdgCacheHome, home, userProfile, ".cache");

    internal static string ResolveDataRoot(
        string? xdgDataHome, string? home, string? userProfile)
        => ResolveXdgRoot(xdgDataHome, home, userProfile, ".local", "share");

    private static string ResolveXdgRoot(
        string? xdgValue, string? home, string? userProfile, params string[] fallbackParts)
    {
        if (IsAbsolute(xdgValue))
            return Path.GetFullPath(xdgValue!);
        return Combine(ResolveHome(home, userProfile), fallbackParts);
    }

    private static bool IsAbsolute(string? path)
        => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    internal static string Combine(string root, params string[] components)
    {
        if (!Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("User path root must be absolute.");
        string normalizedRoot = Path.GetFullPath(root);
        string result = normalizedRoot;
        foreach (string component in components)
        {
            if (string.IsNullOrWhiteSpace(component)
                || component is "." or ".."
                || Path.IsPathFullyQualified(component)
                || component.IndexOfAny(['/', '\\']) >= 0
                || !string.Equals(Path.GetFileName(component), component,
                    StringComparison.Ordinal))
                throw new ArgumentException(
                    "User path components must be simple relative names.");
            result = Path.Combine(result, component);
        }
        string normalizedResult = Path.GetFullPath(result);
        string containmentPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(normalizedResult, normalizedRoot, StringComparison.Ordinal)
            && !normalizedResult.StartsWith(containmentPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Resolved user path escaped its absolute root.");
        return normalizedResult;
    }
}
