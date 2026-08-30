namespace GHelper.Linux.Helpers;

/// <summary>
/// Process-wide startup contract. Uninstalled binaries remain refused;
/// explicit POC flags select development modes, while a machine installed by
/// the XG Mobile MVP installer may start normally without a command-line flag.
/// </summary>
public static class RuntimeMode
{
    public enum StartupIntent
    {
        Refused,
        PocReadOnly,
        PocFunctional,
        PocSmoke,
        InstalledMvp,
    }

    public const string InstalledMvpMarkerPath = "/etc/ghelper/xg-mobile-mvp.conf";
    private const string InstalledMvpMarkerFormat = "mode=xg-mobile-mvp-v1";
    private const string RootPortPmRulePath =
        "/etc/udev/rules.d/80-ghelper-xg-root-port-pm.rules";

    public static string ModeBanner => _intent switch
    {
        StartupIntent.InstalledMvp => "G-HELPER X13",
        StartupIntent.PocFunctional => "POC FUNCTIONAL",
        _ => "POC READ-ONLY",
    };
    public static string PocBanner => ModeBanner;

    private static StartupIntent _intent = StartupIntent.Refused;
    private static string? _pocRoot;
    private static readonly object _guardLogLock = new();
    private static readonly HashSet<string> _guardLogEntries = new(StringComparer.Ordinal);

    public static StartupIntent Intent => _intent;
    public static bool IsPocReadOnly =>
        _intent is StartupIntent.PocReadOnly or StartupIntent.PocSmoke;
    public static bool IsInstalledMvp => _intent == StartupIntent.InstalledMvp;
    public static bool IsPocFunctional =>
        _intent is StartupIntent.PocFunctional or StartupIntent.InstalledMvp;
    public static bool IsPocMode => IsPocReadOnly || IsPocFunctional;
    public static bool IsPocSmoke => _intent == StartupIntent.PocSmoke;
    public static string? PocRoot => _pocRoot;

    /// <summary>Pure argument classification used by startup and tests.</summary>
    public static StartupIntent ClassifyArguments(
        IReadOnlyList<string> args, bool installedMvp = false)
    {
        if (installedMvp
            && (args.Count == 0
                || (args.Count == 1 && args[0] is "--osk" or "--minimized")))
            return StartupIntent.InstalledMvp;
        if (args.Count != 1)
            return StartupIntent.Refused;
        return args[0] switch
        {
            "--poc-readonly" => StartupIntent.PocReadOnly,
            "--poc-functional" => StartupIntent.PocFunctional,
            "--poc-smoke" => StartupIntent.PocSmoke,
            _ => StartupIntent.Refused,
        };
    }

    /// <summary>
    /// Configure the process before AppConfig, native extraction, Avalonia, or
    /// any platform backend is touched. POC state is intentionally isolated
    /// from the user's real XDG config/cache/data trees.
    /// </summary>
    public static StartupIntent Initialize(IReadOnlyList<string> args)
    {
        _intent = ClassifyArguments(args, IsInstalledMvpMarkerValid());
        if (!IsPocMode)
            return _intent;

        string configRoot;
        string cacheRoot;
        string dataRoot;
        if (IsInstalledMvp)
        {
            string home = RequireAbsoluteHome("installed XG Mobile MVP");
            _pocRoot = Path.Combine(home, ".local", "share", "ghelper");
            configRoot = AbsoluteXdgOrDefault("XDG_CONFIG_HOME", home, ".config");
            cacheRoot = AbsoluteXdgOrDefault("XDG_CACHE_HOME", home, ".cache");
            dataRoot = AbsoluteXdgOrDefault("XDG_DATA_HOME", home, ".local", "share");
        }
        else if (IsPocFunctional)
        {
            string home = RequireAbsoluteHome("functional POC");
            _pocRoot = Path.Combine(home, ".local", "share", "ghelper-poc");
            configRoot = Path.Combine(home, ".config", "ghelper-poc");
            cacheRoot = Path.Combine(home, ".cache", "ghelper-poc");
            dataRoot = Path.Combine(_pocRoot, "data");
        }
        else
        {
            _pocRoot = Path.Combine(
                Path.GetTempPath(), $"ghelper-poc-{Environment.ProcessId}-{Guid.NewGuid():N}");
            configRoot = Path.Combine(_pocRoot, "config");
            cacheRoot = Path.Combine(_pocRoot, "cache");
            dataRoot = Path.Combine(_pocRoot, "data");
        }
        Directory.CreateDirectory(_pocRoot!);
        Directory.CreateDirectory(configRoot);
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(dataRoot);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
#pragma warning disable CA1416
            IEnumerable<string> protectedPaths = IsInstalledMvp
                ? new[] { _pocRoot! }
                : new[] { _pocRoot!, configRoot, cacheRoot, dataRoot };
            foreach (string path in protectedPaths)
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        }
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configRoot);
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", cacheRoot);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataRoot);
        return _intent;
    }

    /// <summary>True only when the process was configured for this exact runtime invocation.</summary>
    public static bool IsConfiguredRuntimeInvocation(IReadOnlyList<string> args)
        => IsPocMode && ClassifyArguments(args, IsInstalledMvp) == _intent;

    private static string RequireAbsoluteHome(string mode)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home))
            throw new InvalidOperationException($"A valid user home is required for the {mode}.");
        return home;
    }

    private static string AbsoluteXdgOrDefault(
        string variable, string home, params string[] fallbackComponents)
    {
        string? configured = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(new[] { home }.Concat(fallbackComponents).ToArray());
    }

    private static bool IsInstalledMvpMarkerValid()
    {
        try
        {
            var info = new FileInfo(InstalledMvpMarkerPath);
            if (!info.Exists || info.LinkTarget != null)
                return false;
            string[] lines = File.ReadAllLines(InstalledMvpMarkerPath);
            if (!lines.Contains(InstalledMvpMarkerFormat, StringComparer.Ordinal)
                || !lines.Contains("model=GV301QH", StringComparer.Ordinal))
                return false;
            var rootPortRule = new FileInfo(RootPortPmRulePath);
            if (!rootPortRule.Exists || rootPortRule.LinkTarget != null)
                return false;
            string product = ReadTrimmed("/sys/class/dmi/id/product_name");
            return product.Contains("GV301QH", StringComparison.OrdinalIgnoreCase)
                && IsTargetedXgRootPortPmActive("/sys/bus/pci/devices")
                && File.Exists("/sys/devices/platform/asus-nb-wmi/egpu_connected")
                && File.Exists("/sys/devices/platform/asus-nb-wmi/egpu_enable");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates the model-specific replacement for the legacy global
    /// pcie_port_pm=off argument. Exactly one AMD/ASUS XG root port must match
    /// and its runtime power policy must be pinned to "on".
    /// </summary>
    public static bool IsTargetedXgRootPortPmActive(string pciDevicesRoot)
    {
        try
        {
            if (!Path.IsPathFullyQualified(pciDevicesRoot)
                || !Directory.Exists(pciDevicesRoot))
                return false;

            int matches = 0;
            bool active = false;
            foreach (string devicePath in Directory.EnumerateDirectories(pciDevicesRoot))
            {
                if (ReadTrimmed(Path.Combine(devicePath, "class")) != "0x060400"
                    || ReadTrimmed(Path.Combine(devicePath, "vendor")) != "0x1022"
                    || ReadTrimmed(Path.Combine(devicePath, "device")) != "0x1633"
                    || ReadTrimmed(Path.Combine(devicePath, "subsystem_vendor")) != "0x1043"
                    || ReadTrimmed(Path.Combine(devicePath, "subsystem_device")) != "0x1662")
                    continue;

                matches++;
                active = ReadTrimmed(Path.Combine(devicePath, "power", "control")) == "on";
            }
            return matches == 1 && active;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Central guard for hardware, OS, service, and persistent mutations.</summary>
    public static bool TryAllowMutation(string operation)
    {
        if (!IsPocReadOnly)
            return true;
        LogGuardOnce("mutation", operation);
        return false;
    }

    /// <summary>Central guard for shell/CLI process launches in the POC.</summary>
    public static bool TryAllowExternalProcess(string operation)
    {
        if (!IsPocReadOnly)
            return true;
        LogGuardOnce("external process", operation);
        return false;
    }

    /// <summary>Fail-closed filter for mutating controls recomputed by refresh paths.</summary>
    public static bool FilterMutationControlEnabled(bool requestedEnabled)
        => !IsPocReadOnly && requestedEnabled;

    /// <summary>
    /// Normal and installed-session modes own a tray icon, so a user close
    /// hides the main window. Explicit development POCs still exit on close.
    /// </summary>
    public static bool ShouldHideMainWindowOnClose(bool appIsShuttingDown)
        => (!IsPocMode || IsInstalledMvp) && !appIsShuttingDown;

    /// <summary>
    /// The installed X13 session app owns no state that must be flushed during
    /// logout. Its hardware backend can be blocked in a kernel read while the
    /// NVIDIA endpoint is changing, so SIGTERM must take the bounded process
    /// exit path instead of waiting for backend disposal.
    /// </summary>
    public static bool UsesBoundedSignalShutdown(StartupIntent intent)
        => intent == StartupIntent.InstalledMvp;

    private static void LogGuardOnce(string kind, string operation)
    {
        string key = kind + "\0" + operation;
        lock (_guardLogLock)
        {
            if (!_guardLogEntries.Add(key))
                return;
        }
        Logger.WriteLine($"{PocBanner}: blocked {kind} '{operation}'");
    }

    /// <summary>
    /// Headless representation of the main-window contract. This is kept free
    /// of Avalonia so --poc-smoke can validate startup on a TTY/CI host.
    /// </summary>
    public static PocUiState BuildPocUiState()
        => new(
            Banner: PocBanner,
            ReadOnlyStatusEnabled: true,
            MutatingControlsEnabled: IsPocFunctional,
            PerformanceProfileEnabled: IsPocFunctional,
            BatteryLimitEnabled: IsPocFunctional,
            AutostartEnabled: IsInstalledMvp,
            InstallerOrUpdaterEnabled: false,
            ExternalProcessesEnabled: IsPocFunctional,
            Model: ReadTrimmed("/sys/class/dmi/id/product_name"),
            Bios: ReadTrimmed("/sys/class/dmi/id/bios_version"),
            Kernel: ReadTrimmed("/proc/sys/kernel/osrelease"),
            PlatformProfile: ReadTrimmed("/sys/firmware/acpi/platform_profile"));

    public static bool ValidatePocSmoke(out PocUiState state)
    {
        state = BuildPocUiState();
        if (!IsPocReadOnly || string.IsNullOrWhiteSpace(_pocRoot))
            return false;

        string root = Path.GetFullPath(_pocRoot);
        bool IsUnderPocRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            string full = Path.GetFullPath(path);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        return state.Banner == PocBanner
            && state.ReadOnlyStatusEnabled
            && !state.MutatingControlsEnabled
            && !state.PerformanceProfileEnabled
            && !state.BatteryLimitEnabled
            && !state.AutostartEnabled
            && !state.InstallerOrUpdaterEnabled
            && !state.ExternalProcessesEnabled
            && !FilterMutationControlEnabled(true)
            && !ShouldHideMainWindowOnClose(appIsShuttingDown: false)
            && !TryAllowMutation("poc-smoke-probe")
            && !TryAllowExternalProcess("poc-smoke-probe")
            && IsUnderPocRoot(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"))
            && IsUnderPocRoot(Environment.GetEnvironmentVariable("XDG_CACHE_HOME"))
            && IsUnderPocRoot(Environment.GetEnvironmentVariable("XDG_DATA_HOME"));
    }

    private static string ReadTrimmed(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "unavailable";
        }
        catch
        {
            return "unavailable";
        }
    }
}

public sealed record PocUiState(
    string Banner,
    bool ReadOnlyStatusEnabled,
    bool MutatingControlsEnabled,
    bool PerformanceProfileEnabled,
    bool BatteryLimitEnabled,
    bool AutostartEnabled,
    bool InstallerOrUpdaterEnabled,
    bool ExternalProcessesEnabled,
    string Model,
    string Bios,
    string Kernel,
    string PlatformProfile);
