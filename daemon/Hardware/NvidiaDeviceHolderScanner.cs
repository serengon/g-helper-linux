namespace GHelper.Daemon.Hardware;

/// <summary>
/// Root-side, read-only scan of processes that keep NVIDIA character devices
/// open. This is a best-effort early check; the module-unload gate remains
/// authoritative because the sandboxed daemon intentionally lacks ptrace.
/// </summary>
public static class NvidiaDeviceHolderScanner
{
    public readonly record struct Holder(int Pid, string Comm, int DeviceFdCount)
    {
        public override string ToString() => $"{Pid}/{Comm}";
    }

    public static Holder[] FindHolders(
        string procRoot = "/proc",
        string devRoot = "/dev",
        string sysRoot = "/sys")
    {
        HashSet<string> deviceNodes = FindNvidiaDeviceNodes(devRoot, sysRoot);
        if (deviceNodes.Count == 0 || !Directory.Exists(procRoot))
            return [];

        var holders = new List<Holder>();
        IEnumerable<string> processDirs;
        try
        {
            processDirs = Directory.EnumerateDirectories(procRoot);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        foreach (string processDir in processDirs)
        {
            string pidText = Path.GetFileName(processDir);
            if (!int.TryParse(pidText, out int pid))
                continue;

            int fdCount = 0;
            try
            {
                // /proc/PID/fd entries point at character devices. Enumerate
                // every filesystem entry: EnumerateFiles can omit device-link
                // entries depending on the underlying d_type reported by procfs.
                foreach (string fd in Directory.EnumerateFileSystemEntries(
                    Path.Combine(processDir, "fd")))
                {
                    string? target = File.ResolveLinkTarget(
                        fd, returnFinalTarget: false)?.FullName;
                    if (target is null)
                        continue;
                    if (!Path.IsPathRooted(target))
                        target = Path.GetFullPath(target, Path.GetDirectoryName(fd)!);
                    if (deviceNodes.Contains(target))
                        fdCount++;
                }
            }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (fdCount == 0)
                continue;
            string comm = TryRead(Path.Combine(processDir, "comm"))?.Trim() ?? "?";
            holders.Add(new Holder(pid, comm, fdCount));
        }

        return holders
            .OrderBy(holder => holder.Pid)
            .ToArray();
    }

    private static HashSet<string> FindNvidiaDeviceNodes(string devRoot, string sysRoot)
    {
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(devRoot, "nvidia*"))
            {
                if (!Directory.Exists(path))
                    nodes.Add(Path.GetFullPath(path));
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        string drmClass = Path.Combine(sysRoot, "class", "drm");
        string driRoot = Path.Combine(devRoot, "dri");
        try
        {
            foreach (string drmNode in Directory.EnumerateDirectories(drmClass))
            {
                string name = Path.GetFileName(drmNode);
                if (!(name.StartsWith("card", StringComparison.Ordinal)
                    || name.StartsWith("renderD", StringComparison.Ordinal)))
                    continue;
                string? vendor = TryRead(Path.Combine(drmNode, "device", "vendor"))?.Trim();
                if (vendor == "0x10de")
                    nodes.Add(Path.GetFullPath(Path.Combine(driRoot, name)));
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        string i2cClass = Path.Combine(sysRoot, "bus", "i2c", "devices");
        try
        {
            foreach (string adapter in Directory.EnumerateDirectories(i2cClass, "i2c-*"))
            {
                string? name = TryRead(Path.Combine(adapter, "name"));
                if (name?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true)
                    nodes.Add(Path.GetFullPath(Path.Combine(devRoot, Path.GetFileName(adapter))));
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return nodes;
    }

    private static string? TryRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
