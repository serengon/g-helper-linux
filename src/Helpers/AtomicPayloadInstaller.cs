using System.Security.Cryptography;

namespace GHelper.Linux.Helpers;

/// <summary>
/// Installs an already authenticated embedded payload without ever exposing a
/// partially-written target. Callers may only use the returned path when this
/// method succeeds.
/// </summary>
internal static class AtomicPayloadInstaller
{
    internal delegate bool PayloadWriter(FileStream stream, ReadOnlyMemory<byte> payload);

    internal static string ContentAddressedPath(
        string cacheRoot, string resourceName, ReadOnlySpan<byte> payload)
    {
        if (!Path.IsPathFullyQualified(cacheRoot))
            throw new ArgumentException("cache root must be absolute", nameof(cacheRoot));
        if (string.IsNullOrWhiteSpace(resourceName)
            || Path.GetFileName(resourceName) != resourceName)
            throw new ArgumentException("resource name must be a single path component", nameof(resourceName));
        string digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        return Path.Combine(cacheRoot, digest, resourceName);
    }

    internal static void EnsurePrivateDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("cache directory must be absolute", nameof(directory));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
#pragma warning disable CA1416
        File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
    }

    internal static bool EnsureExact(
        ReadOnlyMemory<byte> payload,
        string targetPath,
        bool executable,
        out string error,
        PayloadWriter? writer = null)
    {
        error = "";
        if (!Path.IsPathFullyQualified(targetPath))
        {
            error = "target path must be absolute";
            return false;
        }
        string expectedHash = Convert.ToHexString(SHA256.HashData(payload.Span));
        string? directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(directory))
        {
            error = "target has no parent directory";
            return false;
        }

        try
        {
            // Correct the private cache-directory mode before trusting an
            // existing entry; otherwise another user could replace the path
            // between validation and the caller's load/spawn operation.
            EnsurePrivateDirectory(directory);
            if (File.Exists(targetPath)
                && string.Equals(HashFile(targetPath), expectedHash, StringComparison.Ordinal))
            {
                SetMode(targetPath, executable);
                return true;
            }
        }
        catch (Exception ex)
        {
            error = $"cached payload validation failed: {ex.Message}";
            return false;
        }

        string tempPath = Path.Combine(directory,
            $".{Path.GetFileName(targetPath)}.tmp.{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, FileOptions.WriteThrough))
            {
                bool complete;
                if (writer == null)
                {
                    stream.Write(payload.Span);
                    complete = true;
                }
                else
                {
                    complete = writer(stream, payload);
                }

                stream.Flush(flushToDisk: true);
                if (!complete || stream.Length != payload.Length)
                    throw new IOException($"incomplete payload write ({stream.Length}/{payload.Length})");
            }

            if (!string.Equals(HashFile(tempPath), expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException("temporary payload digest mismatch");

            SetMode(tempPath, executable);
            File.Move(tempPath, targetPath, overwrite: true);

            if (!string.Equals(HashFile(targetPath), expectedHash, StringComparison.Ordinal))
            {
                File.Delete(targetPath);
                throw new InvalidDataException("installed payload digest mismatch");
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch { }
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void SetMode(string path, bool executable)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

#pragma warning disable CA1416
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (executable)
            mode |= UnixFileMode.UserExecute;
        File.SetUnixFileMode(path, mode);
#pragma warning restore CA1416
    }
}
