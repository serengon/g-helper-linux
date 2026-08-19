using Avalonia;
using Avalonia.Skia;
using Avalonia.X11;
using GHelper.Linux;
using GHelper.Linux.Cli;
using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before ResourceExtractorCli/AppConfig/native extraction so
        // development modes are isolated and installed MVP state is selected.
        RuntimeMode.Initialize(args);

        // Uninstalled review artifacts remain non-deployable. Dispatch safe
        // metadata, exact development flags, or a verified installed MVP.
        var rc = ResourceExtractorCli.TryDispatch(args);
        if (rc.HasValue)
        {
            Environment.Exit(rc.Value);
            return;
        }

        if (RuntimeMode.IsPocSmoke)
        {
            bool ok = RuntimeMode.ValidatePocSmoke(out var state);
            Console.Out.WriteLine(ok ? "POC_SMOKE_OK" : "POC_SMOKE_FAILED");
            Console.Out.WriteLine($"mode={RuntimeMode.PocBanner}");
            Console.Out.WriteLine($"model={state.Model}");
            Console.Out.WriteLine($"bios={state.Bios}");
            Console.Out.WriteLine($"kernel={state.Kernel}");
            Console.Out.WriteLine($"platform_profile={state.PlatformProfile}");
            Console.Out.WriteLine("mutations=blocked");
            Console.Out.WriteLine("external_processes=blocked");
            Environment.Exit(ok ? 0 : 70);
            return;
        }

        // Early-start systemd units (COSMIC autostart) may lack session vars;
        // import them from the systemd user manager before anything reads them.
        if (!RuntimeMode.IsPocMode
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")))
            Cosmic.ImportSessionEnvironment();

        SetGpuPreferenceEnv();

        // "ghelper --osk" toggles the on-screen keyboard of a running
        // instance (hotkey/controller-chord friendly). When no instance is
        // running, normal startup continues and App opens the keyboard.
        if (args.Contains("--osk") && CommandIpc.TrySend("toggle-osk"))
            return;

        // Last-resort logging: capture fatal exceptions to the log file before
        // the process dies (stderr is swallowed under Steam game mode).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.WriteLine($"FATAL unhandled exception: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.WriteLine($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        NativeLibExtractor.ExtractAndLoad();

        // Suppress X11 SMLib/ICELib noise on pure-Wayland sessions (COSMIC, niri).
        // Avalonia's X11 backend tries to connect when SESSION_MANAGER is unset
        // but the SM socket doesn't exist; clearing it prevents the attempt.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SESSION_MANAGER")))
            Environment.SetEnvironmentVariable("SESSION_MANAGER", "");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseX11()
            .With(BuildX11Options())
            .UseSkia()
            .UseHarfBuzz()
            .LogToTrace();

    // Rendering backend. Explicit `render_mode` config wins (software | egl |
    // glx). Otherwise: on Wayland the Avalonia X11 backend runs under
    // XWayland, where GLX glXSwapBuffers can wedge the render thread forever
    // in poll() (the UI thread then deadlocks in SyncWaitCompositorBatch on
    // the next paint or window close); default to EGL there, which keeps GPU
    // acceleration without the wedge. Native Xorg keeps Avalonia's default
    // Glx + Software list.
    private static X11PlatformOptions BuildX11Options()
    {
        var opts = new X11PlatformOptions { WmClass = "ghelper" };
        string mode = AppConfig.GetString("render_mode") ?? "";
        bool wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        opts.RenderingMode = mode switch
        {
            "software" => [X11RenderingMode.Software],
            "egl" => [X11RenderingMode.Egl, X11RenderingMode.Software],
            "glx" => [X11RenderingMode.Glx, X11RenderingMode.Software],
            _ when wayland => [X11RenderingMode.Egl, X11RenderingMode.Software],
            _ => opts.RenderingMode,
        };
        return opts;
    }

    private static void SetGpuPreferenceEnv()
    {
        SetIfUnset("__NV_PRIME_RENDER_OFFLOAD", "0");
        SetIfUnset("__GLX_VENDOR_LIBRARY_NAME", "mesa");
        SetIfUnset("DRI_PRIME", "0");
    }

    private static void SetIfUnset(string name, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, value);
    }
}
