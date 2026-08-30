using GHelper.Linux.Gpu.NVidia;
using GHelper.Linux.Cli;
using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;
using System.Security.Cryptography;
using System.Text;
using static GHelper.Linux.Tests.Harness;

namespace GHelper.Linux.Tests;

public static class SecurityScenarios
{
    public static void RunAll()
    {
        Console.WriteLine("\n Phase 1 fail-closed security ");
        AtomicPayload_StaleCacheIsReplaced();
        AtomicPayload_InterruptedWriteIsRejected();
        AtomicPayload_ConcurrentVersionsRemainIsolated();
        ExternalPrivilegedHelpers_AreUnavailable();
        NixHelperDiscovery_IsDisabled();
        RyzenAdjFeature_IsDisabled();
        UserPaths_EmptyXdgUsesAbsoluteHomeFallback();
        UserPaths_RelativeXdgUsesAbsoluteProfileFallback();
        UserPaths_AbsoluteXdgValuesAreHonored();
        UserPaths_AllInvalidFailsClosed();
        AtomicPayload_RelativeRootsAreRejected();
        UserPaths_TraversalAndSeparatorsAreRejected();
        MetadataCli_UnstampedAssemblyReturns70();
        MetadataCli_MalformedArityReturns64();
        MetadataCli_IncoherentLocalProvenanceIsRejected();
    }

    private static void AtomicPayload_StaleCacheIsReplaced()
        => Scenario(nameof(AtomicPayload_StaleCacheIsReplaced), sb =>
        {
            string dir = Path.Combine(sb.TempRoot, "atomic-stale");
            string target = Path.Combine(dir, "payload");
            Directory.CreateDirectory(dir);
            File.WriteAllText(target, "stale executable");
            byte[] expected = "current embedded payload"u8.ToArray();

            bool ok = AtomicPayloadInstaller.EnsureExact(
                expected, target, executable: true, out string error);

            Assert(ok, "replacement must succeed: " + error);
            Assert(File.ReadAllBytes(target).SequenceEqual(expected),
                "stale cache was not replaced by exact embedded content");
            Assert(!Directory.EnumerateFiles(dir, ".payload.tmp.*").Any(),
                "temporary extraction file remained");
        });

    private static void AtomicPayload_InterruptedWriteIsRejected()
        => Scenario(nameof(AtomicPayload_InterruptedWriteIsRejected), sb =>
        {
            string dir = Path.Combine(sb.TempRoot, "atomic-interrupted");
            string target = Path.Combine(dir, "payload");
            Directory.CreateDirectory(dir);
            byte[] stale = "stale payload"u8.ToArray();
            byte[] expected = "new exact embedded payload"u8.ToArray();
            File.WriteAllBytes(target, stale);

            bool ok = AtomicPayloadInstaller.EnsureExact(
                expected, target, executable: true, out _,
                (stream, payload) =>
                {
                    stream.Write(payload.Span[..(payload.Length / 2)]);
                    return false;
                });

            Assert(!ok, "interrupted extraction must fail closed");
            Assert(File.ReadAllBytes(target).SequenceEqual(stale),
                "interrupted extraction must not replace the prior inode");
            Assert(!Directory.EnumerateFiles(dir, ".payload.tmp.*").Any(),
                "interrupted extraction temporary file remained");
        });

    private static void AtomicPayload_ConcurrentVersionsRemainIsolated()
        => Scenario(nameof(AtomicPayload_ConcurrentVersionsRemainIsolated), sb =>
        {
            string root = Path.Combine(sb.TempRoot, "atomic-content-addressed");
            byte[] versionA = "native payload version A"u8.ToArray();
            byte[] versionB = "native payload version B"u8.ToArray();
            string pathA = AtomicPayloadInstaller.ContentAddressedPath(root, "helper", versionA);
            string pathB = AtomicPayloadInstaller.ContentAddressedPath(root, "helper", versionB);
            Assert(pathA != pathB, "different payload hashes shared a cache path");

            Parallel.For(0, 64, i =>
            {
                byte[] payload = (i & 1) == 0 ? versionA : versionB;
                string path = (i & 1) == 0 ? pathA : pathB;
                if (!AtomicPayloadInstaller.EnsureExact(
                        payload, path, executable: true, out string error))
                    throw new AssertException("concurrent install failed: " + error);
            });

            Assert(File.ReadAllBytes(pathA).SequenceEqual(versionA),
                "version A changed after concurrent version B installation");
            Assert(File.ReadAllBytes(pathB).SequenceEqual(versionB),
                "version B changed after concurrent version A installation");
            Assert(!Directory.EnumerateFiles(root, ".*.tmp.*", SearchOption.AllDirectories).Any(),
                "concurrent install left a temporary payload");
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
#pragma warning disable CA1416
                AssertEqual(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.GetDirectoryName(pathA)!),
                    "version A cache directory mode");
                AssertEqual(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(pathA),
                    "version A executable mode");
#pragma warning restore CA1416
            }
        });

    private static void ExternalPrivilegedHelpers_AreUnavailable()
        => Scenario(nameof(ExternalPrivilegedHelpers_AreUnavailable), sb =>
        {
            AssertEqual("", SysfsHelper.GpuHelperPath,
                "untrusted gpu-helper path");
            AssertEqual("", SysfsHelper.RyzenadjPath,
                "untrusted ryzenadj path");
            Assert(!NvidiaProcessScanner.EnsureHelper(),
                "gpu-helper must remain unavailable");
            AssertEqual(HelperState.Missing, NvidiaProcessScanner.CheckHelper(),
                "gpu-helper state");
            Assert(SysfsHelper.RunSudoOrPkexec("/bin/true", []) == null,
                "legacy privilege runner must fail closed");
        });

    private static void NixHelperDiscovery_IsDisabled()
        => Scenario(nameof(NixHelperDiscovery_IsDisabled), sb =>
        {
            Assert(NixOS.ResolveGpuHelper() == null, "Nix gpu-helper discovery enabled");
            Assert(NixOS.ResolveGpuBlockHelper() == null, "Nix block-helper discovery enabled");
            Assert(NixOS.ResolveRyzenadj() == null, "Nix ryzenadj discovery enabled");
        });

    private static void RyzenAdjFeature_IsDisabled()
        => Scenario(nameof(RyzenAdjFeature_IsDisabled), sb =>
        {
            Assert(!RyzenPower.Available, "RyzenAdj feature unexpectedly available");
            Assert(RyzenPower.Probe() == null, "RyzenAdj probe must be disabled");
            Assert(!RyzenPower.Apply([("stapm-limit", 25_000)]),
                "RyzenAdj apply must be disabled");
            Assert(!RyzenPower.TrySetPpt("ppt_pl1_spl", 25),
                "RyzenAdj PPT fallback must be disabled");
        });

    private static void UserPaths_EmptyXdgUsesAbsoluteHomeFallback()
        => Scenario(nameof(UserPaths_EmptyXdgUsesAbsoluteHomeFallback), sb =>
        {
            string home = Path.Combine(sb.TempRoot, "home-empty");
            string actual = AbsoluteUserPaths.ResolveConfigRoot("", home, "relative-profile");
            AssertEqual(Path.Combine(home, ".config"), actual,
                "empty XDG_CONFIG_HOME fallback");
            Assert(Path.IsPathFullyQualified(actual), "empty-XDG result is relative");
        });

    private static void UserPaths_RelativeXdgUsesAbsoluteProfileFallback()
        => Scenario(nameof(UserPaths_RelativeXdgUsesAbsoluteProfileFallback), sb =>
        {
            string profile = Path.Combine(sb.TempRoot, "profile-relative-xdg");
            string actual = AbsoluteUserPaths.ResolveConfigRoot(
                "relative/config", "relative-home", profile);
            AssertEqual(Path.Combine(profile, ".config"), actual,
                "relative XDG_CONFIG_HOME fallback");
            Assert(Path.IsPathFullyQualified(actual), "relative-XDG result is relative");
        });

    private static void UserPaths_AbsoluteXdgValuesAreHonored()
        => Scenario(nameof(UserPaths_AbsoluteXdgValuesAreHonored), sb =>
        {
            string config = Path.Combine(sb.TempRoot, "xdg-config");
            string cache = Path.Combine(sb.TempRoot, "xdg-cache");
            string data = Path.Combine(sb.TempRoot, "xdg-data");
            AssertEqual(config,
                AbsoluteUserPaths.ResolveConfigRoot(config, "relative", "relative"),
                "absolute XDG_CONFIG_HOME");
            AssertEqual(cache,
                AbsoluteUserPaths.ResolveCacheRoot(cache, "relative", "relative"),
                "absolute XDG_CACHE_HOME");
            AssertEqual(data,
                AbsoluteUserPaths.ResolveDataRoot(data, "relative", "relative"),
                "absolute XDG_DATA_HOME");
        });

    private static void UserPaths_AllInvalidFailsClosed()
        => Scenario(nameof(UserPaths_AllInvalidFailsClosed), sb =>
        {
            bool configFailed = false;
            bool cacheFailed = false;
            try { _ = AbsoluteUserPaths.ResolveConfigRoot("relative", "", "relative"); }
            catch (InvalidOperationException) { configFailed = true; }
            try { _ = AbsoluteUserPaths.ResolveCacheRoot("", "relative", ""); }
            catch (InvalidOperationException) { cacheFailed = true; }
            Assert(configFailed, "invalid config roots did not fail closed");
            Assert(cacheFailed, "invalid cache roots did not fail closed");
        });

    private static void AtomicPayload_RelativeRootsAreRejected()
        => Scenario(nameof(AtomicPayload_RelativeRootsAreRejected), sb =>
        {
            bool contentPathFailed = false;
            try
            {
                _ = AtomicPayloadInstaller.ContentAddressedPath(
                    "relative-cache", "helper", "payload"u8);
            }
            catch (ArgumentException) { contentPathFailed = true; }
            bool installOk = AtomicPayloadInstaller.EnsureExact(
                "payload"u8.ToArray(), "relative-cache/helper", true, out string error);
            Assert(contentPathFailed, "content-addressed relative root was accepted");
            Assert(!installOk && error.Contains("absolute", StringComparison.Ordinal),
                "atomic installer accepted a relative target");
        });

    private static void UserPaths_TraversalAndSeparatorsAreRejected()
        => Scenario(nameof(UserPaths_TraversalAndSeparatorsAreRejected), sb =>
        {
            string root = Path.Combine(sb.TempRoot, "absolute-root");
            foreach (string component in new[] { "..", ".", "a/b", "a\\b", "/etc" })
            {
                bool rejected = false;
                try { _ = AbsoluteUserPaths.Combine(root, component); }
                catch (ArgumentException) { rejected = true; }
                Assert(rejected, $"unsafe component accepted: {component}");
            }
            string safe = AbsoluteUserPaths.Combine(root, "ghelper", "libs");
            Assert(safe.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.Ordinal), "safe path escaped its root");
        });

    private static void MetadataCli_UnstampedAssemblyReturns70()
        => Scenario(nameof(MetadataCli_UnstampedAssemblyReturns70), sb =>
        {
            int? rc = ResourceExtractorCli.TryDispatch(["--print-build-metadata"]);
            Assert(rc == 70, "unstamped test assembly metadata exit code");
        });

    private static void MetadataCli_MalformedArityReturns64()
        => Scenario(nameof(MetadataCli_MalformedArityReturns64), sb =>
        {
            TextWriter original = Console.Error;
            try
            {
                using var sink = new StringWriter();
                Console.SetError(sink);
                Assert(ResourceExtractorCli.TryDispatch(
                    ["--print-build-metadata", "extra"]) == 64,
                    "malformed metadata arity exit code");
            }
            finally
            {
                Console.SetError(original);
            }
        });

    private static void MetadataCli_IncoherentLocalProvenanceIsRejected()
        => Scenario(nameof(MetadataCli_IncoherentLocalProvenanceIsRejected), sb =>
        {
            string source = new('a', 40);
            string imageId = "sha256:" + new string('b', 64);
            string imageInput = new('c', 64);
            string cacheInput = new('f', 64);
            string manifest = "locked-package|0644|1|" + new string('d', 64) + "\n";
            string nugetHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
            string environmentInput =
                "format=ghelper-phase1-environment-v2\n"
                + $"image_id={imageId}\n"
                + $"image_input_sha256={imageInput}\n"
                + $"cache_input_sha256={cacheInput}\n"
                + $"nuget_manifest_sha256={nugetHash}\n";
            string environmentHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(environmentInput))).ToLowerInvariant();
            string version = $"1.0.90-x13.1+{source}.env.{environmentHash}";
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GHelperBuildImageId"] = imageId,
                ["GHelperImageInputSha256"] = imageInput,
                ["GHelperCacheInputSha256"] = cacheInput,
                ["GHelperNuGetManifestSha256"] = nugetHash,
                ["GHelperBuildEnvironmentSha256"] = environmentHash,
                ["GHelperSourceProvenance"] = source,
                ["GHelperBuildMode"] = "clean"
            };
            string context =
                "format=ghelper-phase1-build-context-v1\n"
                + $"source_provenance={source}\n"
                + "build_mode=clean\n"
                + $"informational_version={version}\n"
                + $"image_id={imageId}\n"
                + $"image_input_sha256={imageInput}\n"
                + $"cache_input_sha256={cacheInput}\n"
                + $"nuget_manifest_sha256={nugetHash}\n"
                + $"environment_sha256={environmentHash}\n"
                + "nuget_manifest_begin\n"
                + manifest
                + "nuget_manifest_end\n";

            Assert(ResourceExtractorCli.ValidateLocalProvenance(
                    version, new Version(1, 0, 90, 0), metadata, context),
                "coherent synthetic local provenance was rejected");

            var wrongMode = new Dictionary<string, string>(metadata, StringComparer.Ordinal)
            {
                ["GHelperBuildMode"] = "dirty-review"
            };
            Assert(!ResourceExtractorCli.ValidateLocalProvenance(
                    version, new Version(1, 0, 90, 0), wrongMode, context),
                "local metadata with incoherent build mode was accepted");

            var wrongSource = new Dictionary<string, string>(metadata, StringComparer.Ordinal)
            {
                ["GHelperSourceProvenance"] = new string('e', 40)
            };
            Assert(!ResourceExtractorCli.ValidateLocalProvenance(
                    version, new Version(1, 0, 90, 0), wrongSource, context),
                "local metadata with incoherent source provenance was accepted");
            Assert(!ResourceExtractorCli.ValidateLocalProvenance(
                    version, new Version(1, 0, 91, 0), metadata, context),
                "local metadata with incoherent assembly version was accepted");
            Assert(!ResourceExtractorCli.ValidateLocalProvenance(
                    version, new Version(1, 0, 90, 0), metadata,
                    context.Replace("build_mode=clean", "build_mode=dirty-review",
                        StringComparison.Ordinal)),
                "local context with incoherent build mode was accepted");
        });
}
