using GHelper.Linux.Cli;
using GHelper.Linux.Gpu;
using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;
using GHelper.Linux.USB;
using static GHelper.Linux.Tests.Harness;

namespace GHelper.Linux.Tests;

/// <summary>
/// These run last because RuntimeMode is intentionally process-wide and
/// fail-closed once the explicit POC is activated.
/// </summary>
public static class PocReadOnlyScenarios
{
    public static void RunAll()
    {
        Console.WriteLine("\n POC read-only startup and mutation guard ");
        TargetedRootPortPm_ReplacesLegacyGlobalKernelArgument();
        ExactFlags_AreClassifiedAndMalformedFlagsAreRefused();
        PocMode_BlocksCriticalMutationEntrypoints();
    }

    private static void TargetedRootPortPm_ReplacesLegacyGlobalKernelArgument()
        => Scenario(nameof(TargetedRootPortPm_ReplacesLegacyGlobalKernelArgument), sb =>
        {
            string pciRoot = Path.Combine(sb.TempRoot, "pci-devices");
            string port = Path.Combine(pciRoot, "0000:00:01.1");
            Directory.CreateDirectory(Path.Combine(port, "power"));
            File.WriteAllText(Path.Combine(port, "class"), "0x060400\n");
            File.WriteAllText(Path.Combine(port, "vendor"), "0x1022\n");
            File.WriteAllText(Path.Combine(port, "device"), "0x1633\n");
            File.WriteAllText(Path.Combine(port, "subsystem_vendor"), "0x1043\n");
            File.WriteAllText(Path.Combine(port, "subsystem_device"), "0x1662\n");
            File.WriteAllText(Path.Combine(port, "power", "control"), "on\n");

            Assert(RuntimeMode.IsTargetedXgRootPortPmActive(pciRoot),
                "exact XG root-port identity with power/control=on is accepted");
            File.WriteAllText(Path.Combine(port, "power", "control"), "auto\n");
            Assert(!RuntimeMode.IsTargetedXgRootPortPmActive(pciRoot),
                "runtime-suspended XG root port fails closed");
            File.WriteAllText(Path.Combine(port, "power", "control"), "on\n");
            File.WriteAllText(Path.Combine(port, "subsystem_device"), "0xffff\n");
            Assert(!RuntimeMode.IsTargetedXgRootPortPmActive(pciRoot),
                "lookalike PCI bridge is rejected");
        });

    private static void ExactFlags_AreClassifiedAndMalformedFlagsAreRefused()
        => Scenario(nameof(ExactFlags_AreClassifiedAndMalformedFlagsAreRefused), sb =>
        {
            AssertEqual(RuntimeMode.StartupIntent.PocReadOnly,
                RuntimeMode.ClassifyArguments(["--poc-readonly"]),
                "exact readonly flag");
            AssertEqual(RuntimeMode.StartupIntent.PocFunctional,
                RuntimeMode.ClassifyArguments(["--poc-functional"]),
                "exact functional flag");
            AssertEqual(RuntimeMode.StartupIntent.PocSmoke,
                RuntimeMode.ClassifyArguments(["--poc-smoke"]),
                "exact smoke flag");
            AssertEqual(RuntimeMode.StartupIntent.Refused,
                RuntimeMode.ClassifyArguments([]),
                "uninstalled normal launch remains refused");
            AssertEqual(RuntimeMode.StartupIntent.InstalledMvp,
                RuntimeMode.ClassifyArguments([], installedMvp: true),
                "installed MVP accepts normal launch");
            AssertEqual(RuntimeMode.StartupIntent.InstalledMvp,
                RuntimeMode.ClassifyArguments(["--osk"], installedMvp: true),
                "installed MVP accepts OSK follow-up launch");
            AssertEqual(RuntimeMode.StartupIntent.InstalledMvp,
                RuntimeMode.ClassifyArguments(["--minimized"], installedMvp: true),
                "installed MVP accepts minimized session launch");
            AssertEqual(RuntimeMode.StartupIntent.Refused,
                RuntimeMode.ClassifyArguments(["unexpected"], installedMvp: true),
                "installed MVP refuses unknown arguments");
            AssertEqual(RuntimeMode.StartupIntent.Refused,
                RuntimeMode.ClassifyArguments(["--poc-readonly", "extra"]),
                "extra args fail closed");

            RuntimeMode.Initialize(["--poc-readonly"]);
            Assert(ResourceExtractorCli.TryDispatch(["--poc-readonly"]) == null,
                "exact configured POC reaches runtime");
            AssertEqual(64,
                ResourceExtractorCli.TryDispatch(["--poc-readonly", "extra"]),
                "malformed POC invocation remains rc64");

            var ui = RuntimeMode.BuildPocUiState();
            AssertEqual("POC READ-ONLY", ui.Banner, "visible banner contract");
            Assert(ui.ReadOnlyStatusEnabled, "read-only status remains enabled");
            Assert(!ui.MutatingControlsEnabled, "mutating UI controls disabled");
            Assert(!ui.AutostartEnabled, "autostart disabled");
            Assert(!ui.InstallerOrUpdaterEnabled, "installer/updater disabled");
            Assert(!ui.ExternalProcessesEnabled, "external processes disabled");
            Assert(!RuntimeMode.FilterMutationControlEnabled(true),
                "simulated XGM refresh cannot re-enable mutation controls");
            Assert(!RuntimeMode.ShouldHideMainWindowOnClose(appIsShuttingDown: false),
                "POC close is allowed to terminate instead of hiding to tray");
            Assert(RuntimeMode.UsesBoundedSignalShutdown(
                    RuntimeMode.StartupIntent.InstalledMvp),
                "installed X13 signal shutdown is bounded");
            Assert(!RuntimeMode.UsesBoundedSignalShutdown(
                    RuntimeMode.StartupIntent.PocFunctional),
                "development POC retains diagnostic cleanup");
            AssertEqual("quiet", AsusctlControlBridge.ProfileToken(2), "quiet profile mapping");
            AssertEqual("balanced", AsusctlControlBridge.ProfileToken(0), "balanced profile mapping");
            AssertEqual("performance", AsusctlControlBridge.ProfileToken(1), "performance profile mapping");
            Assert(AsusctlControlBridge.ProfileToken(99) == null, "unknown profile refused");
        });

    private static void PocMode_BlocksCriticalMutationEntrypoints()
        => Scenario(nameof(PocMode_BlocksCriticalMutationEntrypoints), sb =>
        {
            string probe = Path.Combine(sb.TempRoot, "must-not-be-written");
            File.WriteAllText(probe, "before");
            Assert(!SysfsHelper.WriteAttribute(probe, "after"),
                "central write guard refuses filesystem/sysfs write");
            AssertEqual("before", File.ReadAllText(probe),
                "write guard leaves target unchanged");

            string processMarker = Path.Combine(sb.TempRoot, "process-must-not-run");
            Assert(SysfsHelper.RunCommand("/usr/bin/touch", processMarker) == null,
                "external process is refused");
            Assert(!File.Exists(processMarker), "refused process did not execute");

            var before = sb.Wmi.Calls.Count;
            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);
            AssertEqual(GpuSwitchResult.Failed, result,
                "GPU mutation entrypoint fails closed");
            AssertEqual(before, sb.Wmi.Calls.Count,
                "GPU mutation guard prevented hardware writes");

            Assert(!XgmMutationGate.Allow("test write"),
                "low-level XGM write transport is refused before HID access");
            Assert(!XgmMutationGate.Allow("test reset"),
                "low-level XGM reset transport is refused before HID access");
        });
}
