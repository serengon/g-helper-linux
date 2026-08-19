using System.Diagnostics;
using System.Text;
using GHelper.Daemon.Contract;
using GHelper.Daemon.Core;

namespace GHelper.Daemon.Hardware;

public sealed class XgMobileMutationExecutor : IMutationExecutor
{
    private const string EnableOperation = "enable-xg-mode";
    private const string DisableOperation = "disable-xg-mode";
    private const string EgpuEnablePath = "/sys/devices/platform/asus-nb-wmi/egpu_enable";
    private const string EgpuConnectedPath = "/sys/devices/platform/asus-nb-wmi/egpu_connected";
    private const string PciDevicesPath = "/sys/bus/pci/devices";
    private const string PciRescanPath = "/sys/bus/pci/rescan";
    private const string InternalNvidiaDeviceId = "0x1f9d";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private int _transitionRunning;

    public bool CanExecute(MutationDefinition mutation)
        => mutation.Operation is EnableOperation or DisableOperation;

    public bool TryQueue(MutationDefinition mutation)
    {
        if (!CanExecute(mutation)
            || Interlocked.CompareExchange(ref _transitionRunning, 1, 0) != 0)
            return false;

        bool enable = mutation.Operation == EnableOperation;
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteTransitionAsync(enable).ConfigureAwait(false);
                Console.WriteLine($"XG Mobile live transition completed: enabled={enable}.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"XG Mobile live transition failed ({ex.GetType().Name}): {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _transitionRunning, 0);
            }
        });
        return true;
    }

    private static async Task ExecuteTransitionAsync(bool enable)
    {
        ValidateMachineAndConnection();

        bool currentlyEnabled = ReadOneOrZero(EgpuEnablePath) == "1";
        string? nvidiaGpu = FindZeroOrOneNvidiaGpu();
        if ((enable && currentlyEnabled && nvidiaGpu is not null)
            || (!enable && !currentlyEnabled && nvidiaGpu is null))
        {
            Console.WriteLine($"XG Mobile already has requested state: enabled={enable}.");
            return;
        }

        string? audioFunction = nvidiaGpu is null ? null : FindAudioFunction(nvidiaGpu);
        bool stateWriteCompleted = false;

        try
        {
            Console.WriteLine($"Starting live XG Mobile transition: {currentlyEnabled} -> {enable}.");
            await RunOptionalAsync("/usr/bin/systemctl", ["stop", "nvidia-powerd.service"])
                .ConfigureAwait(false);
            await RunOptionalAsync("/usr/bin/systemctl", ["stop", "nvidia-persistenced.service"])
                .ConfigureAwait(false);

            // Both directions replace the NVIDIA device behind 01:00.0:
            // GTX 1650 internal <-> RTX 3080 XG Mobile. The driver must be
            // fully detached before asking ACPI to remove either endpoint.
            await WaitForNvidiaDeviceUsersToExitAsync(TimeSpan.FromSeconds(20))
                .ConfigureAwait(false);
            if (audioFunction is not null)
                TryWriteDriverControl(audioFunction, "snd_hda_intel", "unbind");
            await UnloadNvidiaModulesAsync().ConfigureAwait(false);

            if (!enable)
            {
                WriteSysfs(EgpuEnablePath, "0");
                stateWriteCompleted = true;
                await WaitForValueAsync(EgpuEnablePath, "0", TimeSpan.FromSeconds(20))
                    .ConfigureAwait(false);
                if (nvidiaGpu is not null)
                    await WaitForPciDeviceAsync(nvidiaGpu, present: false, TimeSpan.FromSeconds(20))
                        .ConfigureAwait(false);
            }
            else
            {
                try
                {
                    WriteSysfs(EgpuEnablePath, "1");
                    stateWriteCompleted = true;
                }
                catch (IOException ex)
                {
                    // Linux asus-wmi maps a firmware return value greater than
                    // one to EIO. Upstream Windows G-Helper does not branch on
                    // DeviceSet's return value: it still performs XGM.Init and
                    // waits for enumeration. Follow that exact behavior here.
                    Console.Error.WriteLine(
                        $"XG Mobile ACPI enable reported {ex.GetType().Name}; " +
                        "continuing with the upstream HID initialization sequence.");
                }

                if (!XgMobileHid.Initialize())
                    throw new InvalidOperationException("The official XG Mobile HID initialization could not run.");

                // Upstream waits 15 seconds after XGM.Init before recreating
                // GPU control. Linux additionally needs an explicit PCI rescan.
                await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                WriteSysfs(PciRescanPath, "1");
                await RunOptionalAsync("/usr/bin/udevadm", ["settle", "--timeout=20"])
                    .ConfigureAwait(false);
                await WaitForXgNvidiaGpuAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                await RunRequiredAsync("/usr/sbin/modprobe", ["nvidia"], CommandTimeout)
                    .ConfigureAwait(false);
                await WaitForValueAsync(EgpuEnablePath, "1", TimeSpan.FromSeconds(20))
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Never issue a second ACPI write when the first write itself
            // failed: firmware may have partially applied it or left the WMI
            // method blocked. Only roll back after a confirmed completed write.
            if (stateWriteCompleted)
                await TryRestorePreviousStateAsync(currentlyEnabled).ConfigureAwait(false);
            else if (nvidiaGpu is not null)
                await TryRestoreNvidiaStackAsync(audioFunction).ConfigureAwait(false);
            throw;
        }
        finally
        {
            // Mutter is kept on the integrated AMD GPU by the packaged udev
            // rule. The graphical session must remain alive throughout the
            // transition; any NVIDIA holder makes the operation fail closed
            // before the ACPI state is changed.
        }
    }

    private static void ValidateMachineAndConnection()
    {
        string model = ReadRequired("/sys/class/dmi/id/product_name").Trim();
        if (!model.Contains("GV301QH", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported live-XG model '{model}'.");
        if (!File.Exists(EgpuEnablePath) || !File.Exists(EgpuConnectedPath))
            throw new InvalidOperationException("The legacy XG Mobile ACPI controls are absent.");
        if (ReadOneOrZero(EgpuConnectedPath) != "1")
            throw new InvalidOperationException("XG Mobile is not physically connected.");
    }

    private static async Task UnloadNvidiaModulesAsync()
    {
        string[] order = ["nvidia_uvm", "nvidia_drm", "nvidia_modeset", "nvidia_peermem", "nvidia"];
        string[] loaded = order.Where(IsModuleLoaded).ToArray();
        if (loaded.Length == 0)
            return;

        Exception? last = null;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                loaded = order.Where(IsModuleLoaded).ToArray();
                if (loaded.Length == 0)
                    return;
                await RunRequiredAsync("/usr/sbin/modprobe", ["-r", .. loaded], CommandTimeout)
                    .ConfigureAwait(false);
                if (order.All(module => !IsModuleLoaded(module)))
                    return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        string remaining = string.Join(", ", order.Where(IsModuleLoaded));
        throw new InvalidOperationException(
            $"NVIDIA modules are still in use ({remaining}); XG state was not changed.", last);
    }

    private static async Task WaitForNvidiaDeviceUsersToExitAsync(TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            string[] holders = FindNvidiaDeviceHolders();
            if (holders.Length == 0)
                return;
            await Task.Delay(500).ConfigureAwait(false);
        }
        throw new InvalidOperationException(
            $"NVIDIA device nodes are still open: {string.Join(", ", FindNvidiaDeviceHolders())}.");
    }

    private static string[] FindNvidiaDeviceHolders()
    {
        var holders = new HashSet<string>(StringComparer.Ordinal);
        foreach (string processDir in Directory.EnumerateDirectories("/proc"))
        {
            string pid = Path.GetFileName(processDir);
            if (!pid.All(char.IsAsciiDigit))
                continue;
            string fdDir = Path.Combine(processDir, "fd");
            try
            {
                foreach (string fd in Directory.EnumerateFiles(fdDir))
                {
                    string? target = new FileInfo(fd).LinkTarget;
                    if (target is not null && target.StartsWith("/dev/nvidia", StringComparison.Ordinal))
                    {
                        string comm = TryRead(Path.Combine(processDir, "comm"))?.Trim() ?? "?";
                        holders.Add($"{pid}/{comm}");
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return holders.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task TryRestorePreviousStateAsync(bool enabled)
    {
        try
        {
            WriteSysfs(EgpuEnablePath, enabled ? "1" : "0");
            if (enabled)
            {
                WriteSysfs(PciRescanPath, "1");
                await TryRestoreNvidiaStackAsync(null).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"XG rollback failed ({ex.GetType().Name}).");
        }
    }

    private static async Task TryRestoreNvidiaStackAsync(string? audioFunction)
    {
        try
        {
            await RunOptionalAsync("/usr/sbin/modprobe", ["nvidia"]).ConfigureAwait(false);
            if (audioFunction is not null)
                TryWriteDriverControl(audioFunction, "snd_hda_intel", "bind");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"NVIDIA rollback failed ({ex.GetType().Name}).");
        }
    }

    private static string FindSingleNvidiaGpu()
    {
        string[] matches = FindNvidiaGpus();
        if (matches.Length != 1)
            throw new InvalidOperationException(
                $"Expected exactly one NVIDIA display function, found {matches.Length}.");
        return matches[0];
    }

    private static string? FindZeroOrOneNvidiaGpu()
    {
        string[] matches = FindNvidiaGpus();
        if (matches.Length > 1)
            throw new InvalidOperationException(
                $"Expected at most one NVIDIA display function, found {matches.Length}.");
        return matches.Length == 0 ? null : matches[0];
    }

    private static string[] FindNvidiaGpus()
        => Directory.EnumerateDirectories(PciDevicesPath)
            .Where(path => string.Equals(TryRead(Path.Combine(path, "vendor"))?.Trim(), "0x10de", StringComparison.Ordinal))
            .Where(path => (TryRead(Path.Combine(path, "class"))?.Trim() ?? string.Empty)
                .StartsWith("0x03", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()
            .ToArray();

    private static string? FindAudioFunction(string gpuFunction)
    {
        int dot = gpuFunction.LastIndexOf('.');
        if (dot < 0)
            return null;
        string audio = gpuFunction[..(dot + 1)] + "1";
        return Directory.Exists(Path.Combine(PciDevicesPath, audio)) ? audio : null;
    }

    private static void TryWriteDriverControl(string pciFunction, string driver, string verb)
    {
        string path = $"/sys/bus/pci/drivers/{driver}/{verb}";
        if (!File.Exists(path))
            return;
        try { WriteSysfs(path, pciFunction); }
        catch (IOException) { }
    }

    private static bool IsModuleLoaded(string module)
        => File.ReadLines("/proc/modules")
            .Any(line => line.StartsWith(module + " ", StringComparison.Ordinal));

    private static async Task WaitForPciDeviceAsync(string pciFunction, bool present, TimeSpan timeout)
    {
        string path = Path.Combine(PciDevicesPath, pciFunction);
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (Directory.Exists(path) == present)
                return;
            await Task.Delay(500).ConfigureAwait(false);
        }
        throw new TimeoutException($"PCI device {pciFunction} present={present} did not settle.");
    }

    private static async Task WaitForXgNvidiaGpuAsync(TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            string[] xgGpus = FindNvidiaGpus()
                .Where(pciFunction => !string.Equals(
                    TryRead(Path.Combine(PciDevicesPath, pciFunction, "device"))?.Trim(),
                    InternalNvidiaDeviceId,
                    StringComparison.Ordinal))
                .ToArray();
            if (xgGpus.Length == 1)
                return;
            if (xgGpus.Length > 1)
                throw new InvalidOperationException("More than one external NVIDIA GPU enumerated.");
            await Task.Delay(500).ConfigureAwait(false);
        }
        throw new TimeoutException("The XG Mobile NVIDIA GPU did not enumerate after enabling.");
    }

    private static async Task WaitForValueAsync(string path, string expected, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (string.Equals(TryRead(path)?.Trim(), expected, StringComparison.Ordinal))
                return;
            await Task.Delay(500).ConfigureAwait(false);
        }
        throw new TimeoutException($"{path} did not become '{expected}'.");
    }

    private static async Task RunRequiredAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {executable}.");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Command timed out: {executable}.");
        }

        string output = (await stdout.ConfigureAwait(false)).Trim();
        string error = (await stderr.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command failed ({process.ExitCode}): {executable}; {error}");
        if (!string.IsNullOrEmpty(output))
            Console.WriteLine(output);
    }

    private static async Task RunOptionalAsync(string executable, IReadOnlyList<string> arguments)
    {
        try { await RunRequiredAsync(executable, arguments, CommandTimeout).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Console.WriteLine($"Optional command skipped/failed: {executable} ({ex.GetType().Name}).");
        }
    }

    private static string ReadOneOrZero(string path)
    {
        string value = ReadRequired(path).Trim();
        if (value is not ("0" or "1"))
            throw new InvalidOperationException($"Unexpected value '{value}' in {path}.");
        return value;
    }

    private static string ReadRequired(string path)
        => File.ReadAllText(path);

    private static string? TryRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void WriteSysfs(string path, string value)
    {
        // File.WriteAllText uses FileMode.Create/O_TRUNC. sysfs attributes are
        // not regular files and may apply the write, then return EIO for the
        // truncate path. Open the existing node O_WRONLY and write once.
        byte[] payload = Encoding.ASCII.GetBytes(value + "\n");
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1);
        stream.Write(payload);
        stream.Flush();
    }
}
