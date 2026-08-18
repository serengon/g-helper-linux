using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GHelper.Linux.Cli;

/// <summary>
/// Phase 1 dispatcher. The review artifact is intentionally non-deployable:
/// only the exact metadata query may succeed and every other invocation is
/// refused before application/runtime initialization.
/// </summary>
public static class ResourceExtractorCli
{
    /// <summary>
    /// Return a terminal exit code for every invocation. There is no normal
    /// GUI/runtime fall-through in Phase 1.
    /// </summary>
    public static int? TryDispatch(string[] args)
    {
        if (args.Length == 1 && args[0] == "--print-build-metadata")
        {
            if (!TryGetLocalReproducibleMetadata(out string value))
                return 70;
            Console.Out.WriteLine("LOCAL-REPRODUCIBLE-UNSIGNED\t" + value);
            return 0;
        }

        Console.Error.WriteLine(
            I18n.Labels.GetForCurrentCultureWithoutInitialization("udev_not_installed"));
        return 64;
    }

    private static bool TryGetLocalReproducibleMetadata(out string informationalVersion)
    {
        try
        {
            return TryGetLocalReproducibleMetadataCore(out informationalVersion);
        }
        catch
        {
            informationalVersion = "";
            return false;
        }
    }

    private static bool TryGetLocalReproducibleMetadataCore(out string informationalVersion)
    {
        informationalVersion = "";
        Assembly assembly = typeof(ResourceExtractorCli).Assembly;
        string version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "";
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Value != null)
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value!,
                StringComparer.Ordinal);

        using Stream? stream = assembly.GetManifestResourceStream(
            "GHelper.Linux.BUILD_ENVIRONMENT");
        if (stream == null)
            return false;
        using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: false);
        string context = reader.ReadToEnd();
        if (!ValidateLocalProvenance(version, assembly.GetName().Version, metadata, context))
            return false;
        informationalVersion = version;
        return true;
    }

    internal static bool ValidateLocalProvenance(
        string informationalVersion,
        Version? assemblyVersion,
        IReadOnlyDictionary<string, string> metadata,
        string context)
    {
        try
        {
            return ValidateLocalProvenanceCore(
                informationalVersion, assemblyVersion, metadata, context);
        }
        catch
        {
            return false;
        }
    }

    private static bool ValidateLocalProvenanceCore(
        string informationalVersion,
        Version? assemblyVersion,
        IReadOnlyDictionary<string, string> metadata,
        string context)
    {
        if (assemblyVersion != new Version(1, 0, 90, 0))
            return false;
        const string versionPattern =
            @"^1\.0\.90-x13\.1\+(?<source>[0-9a-f]{40}|dirty\.[0-9a-f]{64}\.base\.[0-9a-f]{40})\.env\.(?<environment>[0-9a-f]{64})$";
        Match match = Regex.Match(
            informationalVersion, versionPattern, RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;
        string versionSource = match.Groups["source"].Value;
        string versionEnvironment = match.Groups["environment"].Value;
        string inferredMode = versionSource.StartsWith("dirty.", StringComparison.Ordinal)
            ? "dirty-review"
            : "clean";

        if (!metadata.TryGetValue("GHelperBuildImageId", out string? imageId)
            || !Regex.IsMatch(imageId, @"^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)
            || !metadata.TryGetValue("GHelperNuGetManifestSha256", out string? nugetHash)
            || !Regex.IsMatch(nugetHash, @"^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
            || !metadata.TryGetValue("GHelperBuildEnvironmentSha256", out string? environmentHash)
            || !Regex.IsMatch(environmentHash, @"^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
            || !metadata.TryGetValue("GHelperSourceProvenance", out string? sourceProvenance)
            || !metadata.TryGetValue("GHelperImageInputSha256", out string? imageInputHash)
            || !Regex.IsMatch(imageInputHash, @"^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
            || !metadata.TryGetValue("GHelperCacheInputSha256", out string? cacheInputHash)
            || !Regex.IsMatch(cacheInputHash, @"^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
            || !metadata.TryGetValue("GHelperBuildMode", out string? buildMode)
            || !string.Equals(versionEnvironment, environmentHash, StringComparison.Ordinal)
            || !string.Equals(versionSource, sourceProvenance, StringComparison.Ordinal)
            || !string.Equals(inferredMode, buildMode, StringComparison.Ordinal))
            return false;

        string expectedHeader =
            "format=ghelper-phase1-build-context-v1\n";
        if (!context.StartsWith(expectedHeader, StringComparison.Ordinal)
            || context.Contains("\nwrapper_nonce=", StringComparison.Ordinal)
            || !ContextHasExactValue(context, "source_provenance", sourceProvenance)
            || !ContextHasExactValue(context, "build_mode", buildMode)
            || !ContextHasExactValue(context, "informational_version", informationalVersion)
            || !ContextHasExactValue(context, "image_id", imageId)
            || !ContextHasExactValue(context, "image_input_sha256", imageInputHash)
            || !ContextHasExactValue(context, "cache_input_sha256", cacheInputHash)
            || !ContextHasExactValue(context, "nuget_manifest_sha256", nugetHash)
            || !ContextHasExactValue(context, "environment_sha256", environmentHash))
            return false;

        const string begin = "nuget_manifest_begin\n";
        const string end = "nuget_manifest_end\n";
        int beginIndex = context.IndexOf(begin, StringComparison.Ordinal);
        int endIndex = context.IndexOf(end, StringComparison.Ordinal);
        if (beginIndex < 0 || endIndex < beginIndex + begin.Length
            || endIndex + end.Length != context.Length)
            return false;
        string manifest = context[(beginIndex + begin.Length)..endIndex];
        string actualNugetHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        if (!string.Equals(actualNugetHash, nugetHash, StringComparison.Ordinal))
            return false;

        string environmentInput =
            "format=ghelper-phase1-environment-v2\n"
            + $"image_id={imageId}\n"
            + $"image_input_sha256={imageInputHash}\n"
            + $"cache_input_sha256={cacheInputHash}\n"
            + $"nuget_manifest_sha256={nugetHash}\n";
        string actualEnvironmentHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(environmentInput))).ToLowerInvariant();
        if (!string.Equals(actualEnvironmentHash, environmentHash, StringComparison.Ordinal))
            return false;

        return true;
    }

    private static bool ContextHasExactValue(string context, string key, string expected)
        => string.Equals(GetContextValue(context, key), expected, StringComparison.Ordinal);

    private static string? GetContextValue(string context, string key)
    {
        string prefix = key + "=";
        string? found = null;
        foreach (string line in context.Split('\n'))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            if (found != null)
                return null;
            found = line[prefix.Length..];
        }
        return found;
    }
}
