[![G-Helper for Linux](screenshot.png)](screenshot.png)

*Click on the screenshot to view full size.*

> X13 hardened branch: build provenance, locked dependencies, and the pinned
> container workflow are documented in
> [docs/reproducible-build.md](docs/reproducible-build.md).

```
 ██████╗       ██╗  ██╗███████╗██╗     ██████╗ ███████╗██████╗ 
██╔════╝       ██║  ██║██╔════╝██║     ██╔══██╗██╔════╝██╔══██╗
██║  ███╗█████╗███████║█████╗  ██║     ██████╔╝█████╗  ██████╔╝
██║   ██║╚════╝██╔══██║██╔══╝  ██║     ██╔═══╝ ██╔══╝  ██╔══██╗
╚██████╔╝      ██║  ██║███████╗███████╗██║     ███████╗██║  ██║
 ╚═════╝       ╚═╝  ╚═╝╚══════╝╚══════╝╚═╝     ╚══════╝╚═╝  ╚═╝
                        ██╗     ██╗███╗   ██╗██╗   ██╗██╗  ██╗ 
                        ██║     ██║████╗  ██║██║   ██║╚██╗██╔╝ 
                        ██║     ██║██╔██╗ ██║██║   ██║ ╚███╔╝  
                        ██║     ██║██║╚██╗██║██║   ██║ ██╔██╗  
                        ███████╗██║██║ ╚████║╚██████╔╝██╔╝ ██╗ 
                        ╚══════╝╚═╝╚═╝  ╚═══╝ ╚═════╝ ╚═╝  ╚═╝ 
 ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░
         ╔══[ SYSTEM ]════════════════════════════════╗
         ║  >_ KERNEL: LINUX                          ║ 
         ║  >_ STATUS: ONLINE...                      ║
         ╚═══════════════════════════════[ 0x1F4 ]════╝
           ╔══════════════════════════════════════╗
           ║  ASUS LAPTOP CONTROL FOR LINUX       ║
            ╚══════════════════════════════════════╝
```

<div align="center">

[![Changelog](https://img.shields.io/badge/changelog-what's_new-ff8c42?style=for-the-badge)](CHANGELOG.md)

</div>

<a href="https://www.star-history.com/?repos=utajum%2Fg-helper-linux&type=date&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=utajum/g-helper-linux&type=date&theme=dark&legend=top-left" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=utajum/g-helper-linux&type=date&legend=top-left" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=utajum/g-helper-linux&type=date&legend=top-left" />
 </picture>
</a>

## `░▒▓█ ╔══[ WEBSITE ]══╗ █▓▒░`

**[g-helper-linux.elevatech.xyz](https://g-helper-linux.elevatech.xyz)**

---

## `░▒▓█ ╔══[ MOTIVATION ]══╗ █▓▒░`

Since `asusctl` doesn't really care about Ubuntu, I decided to port most functionality from the original [G-Helper](https://github.com/seerge/g-helper) for Windows.

The application is tested on KDE but other desktop environments should also work.

Pull requests and feature requests are welcome!

---

<div align="center">

[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-ffdd00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black)](https://buymeacoffee.com/utajum)

</div>

---

## `░▒▓█ ╔══[ FEATURES ]══╗ █▓▒░`

```
┌──────────────────────────────────────────────────────────────────┐
│  Performance modes         Silent / Balanced / Turbo            │
│  Custom fan curves         8-point drag-to-edit per fan         │
│  Battery charge limit      Protect longevity (40-100%)          │
│  GPU mode switching        Eco / Standard / Optimized (MUX)     │
│  Power limits              CPU PL1/PL2, Dynamic Boost, temps    │
│  Screen control            Refresh rate, Panel OD, MiniLED      │
│  Keyboard backlight        Brightness + RGB color               │
│  Display                   Brightness, gamma adjustment         │
│  Undervolting (AMD)        Curve Optimizer via ryzen_smu         │
│  CPU boost                 Enable/disable turbo boost            │
│  System tray               Background tray icon + context menu  │
│  Auto-start                XDG autostart .desktop integration   │
└──────────────────────────────────────────────────────────────────┘
```

Experimental Lenovo support (IdeaPad / Legion / LOQ / Yoga): performance modes, battery conservation, fan monitoring, PPT limits and keyboard backlight via the mainline ideapad-laptop and lenovo-wmi kernel drivers (kernel 6.17+ for power limits).

---

## `░▒▓█ ╔══[ GPU MODE SWITCHING ]══╗ █▓▒░`

G-Helper manages the ASUS dGPU (discrete GPU) power state and MUX switch through four modes:

```
╔══[ MODES ]═════════════════════════════════════════════════════╗
║                                                                 ║
║  Eco         dGPU powered off (dgpu_disable=1)                 ║
║              Maximum battery life, iGPU only                    ║
║                                                                 ║
║  Standard    Hybrid mode (dgpu_disable=0, MUX=iGPU)            ║
║              dGPU available for offloading, iGPU drives display ║
║                                                                 ║
║  Optimized   Auto-switch based on power source                  ║
║              Eco on battery, Standard on AC power               ║
║                                                                 ║
║  Ultimate    dGPU direct (dgpu_disable=0, MUX=dGPU)            ║
║              Best performance — dGPU drives display directly    ║
║                                                                 ║
╚═════════════════════════════════════════════════════════════════╝
```

### `╠══[ REBOOT REQUIREMENTS ]══╣`

| Transition | Reboot? | Why |
|------------|---------|-----|
| Eco ↔ Standard | Usually no | May need reboot if GPU driver is active |
| Any → Ultimate | **Yes** | MUX switch is latched, takes effect on reboot |
| Ultimate → Any | **Yes** | MUX switch back to iGPU requires reboot |

When a reboot is required, G-Helper shows a notification and the pending mode appears in the UI. You can change your mind before rebooting — clicking a different mode cancels the pending change.

### `╠══[ DRIVER BLOCKING DIALOG ]══╣`

Switching to Eco while the dGPU driver is active shows a dialog with three options:

- **Switch Now** — attempts to unload the dGPU driver (requires admin password)
- **After Reboot** — saves the mode for next boot (dGPU driver is blocked from loading via modprobe rules)
- **Cancel** — keeps the current mode

### `╠══[ KNOWN LIMITATIONS ]══╣`

- **Ultimate → Eco** may require 2 reboots (MUX must change first, then Eco can apply)
- **MUX switch support** varies by model — requires `gpu_mux_mode` sysfs attribute
- **Eco mode** may require a reboot if the dGPU driver holds a DRM file descriptor
- On boot, a systemd oneshot service applies pending GPU mode changes before the display manager starts

### `╠══[ RAW WMI MODE (EXPERIMENTAL) ]══╣`

For 2020-2021 laptops without `dgpu_disable` sysfs, G-Helper supports raw ACPI/WMI calls
via the kernel's debugfs interface. See **[docs/raw-wmi.md](docs/raw-wmi.md)** for details.

### `╠══[ UNDERVOLTING (AMD RYZEN) ]══╣`

Curve Optimizer undervolting for AMD Ryzen CPUs via the [ryzen_smu](https://github.com/amkillam/ryzen_smu) kernel driver. Requires the driver to be installed separately. The feature is hidden unless the driver is loaded and the CPU is supported. See **[docs/amd-undervolting.md](docs/amd-undervolting.md)** for setup, supported CPUs, and details.

---

## `░▒▓█ ╔══[ DISCLAIMER ]══╗ █▓▒░`

```
╔══[ TERMS OF USE ]══════════════════════════════════════════════╗
║                                                                 ║
║  G-Helper Linux interacts directly with ASUS firmware via       ║
║  kernel sysfs attributes and ACPI/WMI methods. These are the   ║
║  same interfaces used by ASUS Armoury Crate on Windows.         ║
║                                                                 ║
║  BY USING THIS SOFTWARE, YOU ACKNOWLEDGE:                       ║
║                                                                 ║
║  1. This software writes to hardware control registers that     ║
║     affect GPU power state, fan speeds, power limits, and       ║
║     MUX switch configuration.                                   ║
║                                                                 ║
║  2. Incorrect or interrupted writes (e.g., power loss during    ║
║     a MUX switch) could leave hardware in an unexpected state.  ║
║     In rare cases, a CMOS reset may be needed to recover.       ║
║                                                                 ║
║  3. Experimental features (marked as such) bypass normal        ║
║     kernel safety checks and should only be enabled if you      ║
║     understand the risks.                                       ║
║                                                                 ║
║  4. This software is provided AS-IS with no warranty.           ║
║     The authors are not responsible for hardware damage.        ║
║                                                                 ║
╚═════════════════════════════════════════════════════════════════╝
```

---

## `░▒▓█ ╔══[ SYSTEM REQUIREMENTS ]══╗ █▓▒░`

```
╔══[ MINIMUM SPEC ]══════════════════════════════════════════════╗
║                                                                 ║
║  OS       Ubuntu 22.04+ / Debian 12+ / Fedora 38+ / Arch      ║
║  Desktop  X11 or Wayland (X11 recommended for full xrandr)     ║
║  Kernel   6.2+ recommended, 6.9+ for all features              ║
║  Module   asus-nb-wmi (loaded by default on ASUS laptops)      ║
║                                                                 ║
╚═════════════════════════════════════════════════════════════════╝
```

```bash
# verify kernel module
lsmod | grep asus
# expected: asus_nb_wmi, asus_wmi
```

### `╠══[ KERNEL FEATURE MATRIX ]══╣`

| Feature | Min Kernel |
|---------|-----------|
| Performance modes, fan speed, battery limit | 5.17 |
| Custom fan curves (8-point) | 5.17 |
| PPT power limits (PL1, PL2, FPPT) | 6.2 |
| GPU MUX switch | 6.1 |
| NVIDIA Dynamic Boost / Temp Target | 6.2 |
| MiniLED mode control | 6.9 |

---

## X13 hardened build status

> **Do not install or launch this branch on a laptop yet.** Phase 1 only
> establishes a reproducible source/build baseline. Its binary is technically
> non-deployable: a normal launch and every runtime/helper command exit with
> code 64 before Avalonia, configuration, autostart, or native payload code can
> initialize. The only accepted invocation is the exact, side-effect-free
> `ghelper --print-build-metadata` query. Upstream udev and all other `install/`
> files are non-executable quarantined inputs and must not be copied to a host.

The application identifies itself as `1.0.90-x13.1`. Runtime self-update,
self-install, self-repair, and self-removal are disabled. The final artifact
is planned to use a signed RPM only after the daemon, polkit, udev, and
hardware-transition phases pass review; no hardened installer exists yet.
Generic helper extraction and embedded installer payloads are also disabled.
External `gpu-helper`, `gpu-block-helper.sh`, and `ryzenadj` executables are not
discovered or executed until a signed RPM package-identity design is reviewed.
Remote changelog images are rendered as links/placeholders rather than fetched.

### Local build

The supported Phase 1 build path uses the pinned container. It does not
require a host .NET installation:

```bash
./build.sh
GHELPER_OFFLINE=1 ./build.sh
```

Those commands require a clean committed tree. For an explicitly non-release
review of local changes, use `GHELPER_DIRTY_REVIEW=1`; the binary records the
SHA-256 of the complete tracked diff and untracked source set. This is unsigned
local provenance, not authentication or proof of who built it.

Each build first copies the exact tracked diff and untracked source set into a
private Git snapshot. Provenance is computed inside that snapshot, compilation
uses only that snapshot, and a post-build invariant rejects any staged change.
Edits to the shared checkout after staging cannot change compiled inputs.
The canonical container image cannot be overridden. The cache key covers all
three lock files plus the relevant projects, props, and `global.json`; only
that exact package closure is copied to the private cache. Links and special
filesystem nodes are rejected. The image ID and complete cache manifest are
embedded in the artifact and their environment digest is part of its
informational version. A plain `dotnet publish -c Release` is unmistakably
refused as `UNATTESTED`, but MSBuild properties are not a security boundary.

The wrapper also emits `.ghelper-build-manifest-v1`, an external unsigned
manifest bound to the artifact SHA-256 and exact source, image, lock closure,
and environment. `--print-build-metadata` labels its result
`LOCAL-REPRODUCIBLE-UNSIGNED`. Canonical verification is therefore:

```bash
GHELPER_DIRTY_REVIEW=1 GHELPER_OFFLINE=1 \
  ./scripts/verify-artifact.sh /absolute/artifact/directory
```

That command independently rebuilds and requires byte-identical output. An
unsigned manifest can be fabricated; it becomes trusted only by reproduction.
The verifier never launches a supplied candidate. It first rebuilds from the
reviewed inputs, requires exact executable bytes and canonical manifest fields,
then queries metadata only from the independently rebuilt binary.
Cryptographic signing is reserved for the future RPM phase.

Inspect the exact artifact provenance without launching the application:

```bash
./dist/ghelper --print-build-metadata
```

The first command prepares the digest-pinned SDK image and external locked
NuGet cache. The second command is fail-closed and runs with container
networking disabled. Use `--output /absolute/path` to preserve an existing
`dist/`. See [docs/reproducible-build.md](docs/reproducible-build.md).

Do not run any file under `install/`, copy `install/90-ghelper.rules`, or
execute any binary from an upstream release. Every quarantined install file has
its executable bit removed and none is embedded or callable by the Phase 1
application/build path.
The upstream NixOS module/package path and floating vendor updater are removed.
There is intentionally no AppImage or automated release workflow.

---

## `░▒▓█ ╔══[ CONFIGURATION ]══╗ █▓▒░`

```
~/.config/ghelper/config.json
```

Same JSON key format as Windows G-Helper — fan curves and mode settings are compatible.

---

## `░▒▓█ ╔══[ PROJECT STRUCTURE ]══╗ █▓▒░`

```
g-helper-linux/
  build.sh                                # Build script (Native AOT)
  install/
    install.sh                            # disabled fail-closed stub
    install-local.sh                      # disabled fail-closed stub
    90-ghelper.rules                      # unaudited upstream udev input
    ghelper.desktop                       # Desktop entry
  src/
    Program.cs                            # Entry point
    App.axaml / App.axaml.cs              # Avalonia app + tray icon
    GHelper.Linux.csproj                  # Project file (AOT config)
    Helpers/
      Logger.cs                           # Console logger
      AppConfig.cs                        # Configuration (JSON, AOT-safe)
    Mode/
      Modes.cs                            # Performance mode definitions
      ModeControl.cs                      # Mode change orchestrator
    Platform/
      Linux/
        SysfsHelper.cs                    # Core sysfs read/write utility
        LinuxAsusWmi.cs                   # asus-wmi sysfs + evdev events
        LinuxPowerManager.cs              # CPU boost, platform profile
        LinuxDisplayControl.cs            # Backlight, xrandr, gamma
        LinuxNvidiaGpuControl.cs          # nvidia-smi monitoring
        LinuxAmdGpuControl.cs             # amdgpu sysfs monitoring
        LinuxAudioControl.cs              # PulseAudio/PipeWire
        LinuxInputHandler.cs              # evdev event forwarding
        LinuxSystemIntegration.cs         # DMI sysfs, XDG autostart
    UI/
      Styles/
        GHelperTheme.axaml                # Dark theme
      Controls/
        FanCurveChart.cs                  # Interactive fan curve chart
      Views/
        MainWindow.axaml / .cs            # Main settings window
        FansWindow.axaml / .cs            # Fan curve editor + power limits
        ExtraWindow.axaml / .cs           # Display, power, system info
      Assets/
        *.png, *.ico                      # Image assets
```

---

## `░▒▓█ ╔══[ ARCHITECTURE ]══╗ █▓▒░`

| Windows (G-Helper) | Linux (this port) |
|---|---|
| `\\.\ATKACPI` DeviceIoControl | `/sys/devices/platform/asus-nb-wmi/` sysfs |
| DSTS (read) / DEVS (write) | `cat` / `echo >` sysfs attributes |
| WMI `Win32_*` queries | `/sys/class/dmi/id/` sysfs |
| `user32.dll` EnumDisplaySettings | `xrandr` CLI |
| NvAPIWrapper.Net | `nvidia-smi` CLI + hwmon sysfs |
| `atiadlxx.dll` (AMD ADL) | amdgpu hwmon sysfs |
| Task Scheduler autostart | XDG `~/.config/autostart/*.desktop` |
| WinForms UI | Avalonia UI (cross-platform) |

---

## `░▒▓█ ╔══[ CREDITS ]══╗ █▓▒░`

- [G-Helper](https://github.com/seerge/g-helper) by seerge
- [Avalonia UI](https://avaloniaui.net/)
- [asus-wmi kernel driver](https://github.com/torvalds/linux/tree/v7.1/drivers/platform/x86)
- [ryzen_smu](https://github.com/amkillam/ryzen_smu) by Leonardo Gates / amkillam
- [RyzenAdj](https://github.com/FlyGoat/RyzenAdj) by FlyGoat (upstream historical input; disabled and not embedded in this fork)

---

## `░▒▓█ ╔══[ LICENSE ]══╗ █▓▒░`

Same license as the original G-Helper project.

---

<div align="center">

[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-ffdd00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black)](https://buymeacoffee.com/utajum)

```
  ░▒▓█ END OF TRANSMISSION █▓▒░
  > SESSION_END :: 0x00000000
```

</div>
