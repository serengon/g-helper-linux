using System.Reflection;
using System.Runtime.InteropServices;

namespace GHelper.Linux.Helpers;

/// <summary>
/// Materializes native resources from this exact managed executable. Cached
/// files are trusted only when their SHA-256 matches the embedded bytes. No
/// executable, library, PATH entry, or stale cache entry is used as fallback.
/// </summary>
public static class NativeLibExtractor
{
    private static readonly string HomeDir = AbsoluteUserPaths.HomeDirectory();

    private static readonly string CacheDir =
        AbsoluteUserPaths.CachePath("ghelper", "libs");

    private static readonly string[] NativeLibs = ["libHarfBuzzSharp.so", "libSkiaSharp.so"];
    private static readonly string[] EagerTools = ["ghelper-audio"];
    private static readonly Dictionary<string, IntPtr> LoadedLibs = new();

    public static void ExtractAndLoad()
    {
        AtomicPayloadInstaller.EnsurePrivateDirectory(CacheDir);
        Logger.WriteLine($"NativeLibExtractor: starting (content-addressed cache={Short(CacheDir)})");

        foreach (var lib in NativeLibs)
        {
            string path = ExtractRequired(lib, executable: false);
            IntPtr handle;
            try
            {
                handle = NativeLibrary.Load(path);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"refusing unverified fallback after {lib} load failure", ex);
            }

            var libName = Path.GetFileNameWithoutExtension(lib);
            LoadedLibs[libName] = handle;
            LoadedLibs[lib] = handle;
            Logger.WriteLine($"NativeLibExtractor: loaded verified embedded {lib}");
        }

        NativeLibrary.SetDllImportResolver(typeof(SkiaSharp.SKPaint).Assembly, ResolveNativeLib);
        NativeLibrary.SetDllImportResolver(typeof(HarfBuzzSharp.Blob).Assembly, ResolveNativeLib);

        foreach (var tool in EagerTools)
            _ = ExtractFromResources(tool, executable: true);

        Logger.WriteLine("NativeLibExtractor: done");
    }

    /// <summary>
    /// Resolve only a verified resource embedded in this executable. Absence or
    /// extraction failure returns null; cached and PATH executables are never
    /// accepted independently.
    /// </summary>
    public static string? FindTool(string toolName)
        => ExtractFromResources(toolName, executable: true);

    private static string ExtractRequired(string resourceName, bool executable)
        => ExtractFromResources(resourceName, executable)
           ?? throw new InvalidOperationException(
               $"required embedded payload unavailable: {resourceName}");

    private static string? ExtractFromResources(string resourceName, bool executable)
    {
        try
        {
            var assembly = typeof(NativeLibExtractor).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Logger.WriteLine($"NativeLibExtractor: embedded resource absent: {resourceName}");
                return null;
            }

            using var payload = new MemoryStream();
            stream.CopyTo(payload);
            byte[] bytes = payload.ToArray();
            string targetPath = AtomicPayloadInstaller.ContentAddressedPath(
                CacheDir, resourceName, bytes);
            if (!AtomicPayloadInstaller.EnsureExact(
                    bytes, targetPath, executable, out string error))
            {
                Logger.WriteLine(
                    $"NativeLibExtractor: refusing {resourceName}; verified extraction failed: {error}");
                return null;
            }

            Logger.WriteLine(
                $"NativeLibExtractor: verified {resourceName} -> {Short(targetPath)} ({FormatSize(payload.Length)})");
            return targetPath;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"NativeLibExtractor: verified extraction failed for {resourceName}: {ex.Message}");
            return null;
        }
    }

    private static IntPtr ResolveNativeLib(
        string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (LoadedLibs.TryGetValue(libraryName, out var handle))
            return handle;
        if (LoadedLibs.TryGetValue(libraryName + ".so", out handle))
            return handle;
        return IntPtr.Zero;
    }

    private static string Short(string path)
    {
        if (!string.IsNullOrEmpty(HomeDir) && path.StartsWith(HomeDir, StringComparison.Ordinal))
        {
            var tail = path[HomeDir.Length..].TrimStart('/');
            return tail.Length == 0 ? "~" : "~/" + tail;
        }
        return path;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return bytes + " B";
        if (bytes < 1024 * 1024)
            return (bytes / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " KiB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MiB";
    }
}
