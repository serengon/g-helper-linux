# XG Mobile MVP privilege boundary

The installed application runs as the desktop user. It does not invoke `sudo`,
`pkexec`, a shell, or `systemctl`. Privileged XG Mobile switching and internal
dGPU Eco/Standard switching are isolated in `ghelperd`, a separate root service
on the system D-Bus.

## D-Bus contract and authorization

- Bus name: `org.ghelper.Daemon1`
- Object: `/org/ghelper/Daemon1`
- Interface: `org.ghelper.Daemon1`
- Enabled XG mutation: `enable-xg-mode` / `disable-xg-mode`
- Enabled internal dGPU mutation: `enable-dgpu-mode` / `disable-dgpu-mode`
- Polkit actions: `org.ghelper.daemon.set-xg-mode` and
  `org.ghelper.daemon.set-gpu-mode`
- Observable mutation API: `StartMutation(operation) -> job_id` and
  `GetMutationStatus(job_id)`

The daemon takes the unique D-Bus sender from the received message and resolves
its UID/PID through the bus. Polkit authorizes that unique bus name as the active
local session. Caller-provided identity is never accepted. Other declared future
operations still return unsupported. Both actions allow the active local
session without a password prompt; inactive and remote sessions remain denied.

The XG executor is intentionally model-specific. It requires a GV301QH, the ASUS
`egpu_connected` and `egpu_enable` attributes, exactly one visible NVIDIA GPU,
and no process retaining NVIDIA, NVIDIA-owned DRM, or NVIDIA I2C device nodes.
It never kills an application. It owns
NVIDIA module release, HDMI-audio unbind, ASUS WMI transition, XG HID reports,
PCI rescan, and final endpoint verification.

The internal dGPU executor uses the same serialized transition boundary. It
requires the exact internal NVIDIA device `10de:1f9d`, refuses to run while the
XG is active, verifies that no process retains `/dev/nvidia*`, releases the
NVIDIA modules and HDMI-audio function before Eco, and verifies firmware, PCI
presence, and NVIDIA driver binding before reporting success. MUX support is a
separate capability: the GUI exposes Ultimate only when the firmware publishes
`gpu_mux_mode`; no model name is hardcoded to manufacture or suppress it.

Before requesting either transition, the GUI scans the same device classes and
opens a modal preflight when an application is holding the GPU. The user can
cancel, close a process gracefully, force-close it after an explicit warning,
or retry. Process termination is never automatic. The daemon performs a
best-effort repeat scan before queueing. Its systemd sandbox deliberately lacks
`CAP_SYS_PTRACE`, so procfs can hide desktop-user descriptors from that scan;
the authoritative final gate is NVIDIA module release, still before any ACPI
write. A holder found by either gate becomes the stable `GpuInUse` result.

Accepted operations receive an opaque job ID. Status progresses through
`queued`, `running`, and one terminal state: `applied`, `blocked`, or `failed`.
The GUI follows that state instead of assuming that a fire-and-forget request
succeeded or waiting a fixed delay. The original `RequestMutation` method stays
available for compatibility, but new GPU UI paths use the observable API.

## Installed system assets

`scripts/ghelper-xg-mvp.sh install DIST_DIR USER` installs:

```text
/opt/ghelper-xg-mvp/
/usr/libexec/ghelper/ghelperd
/etc/systemd/system/ghelperd.service
/etc/dbus-1/system.d/org.ghelper.Daemon1.conf
/usr/share/polkit-1/actions/org.ghelper.daemon.policy
/etc/udev/rules.d/61-mutter-ignore-x13-nvidia.rules
/etc/udev/rules.d/62-ghelper-x13-user-devices.rules
/etc/udev/rules.d/80-ghelper-xg-root-port-pm.rules
/etc/systemd/user/xdg-desktop-portal-gnome.service.d/50-ghelper-x13-integrated-gpu.conf
/usr/lib/systemd/system-sleep/ghelper-xg-suspend
/usr/libexec/ghelper/ghelper-xg-hid-power.py
/etc/modprobe.d/ghelper-xg-suspend.conf
/etc/ghelper/xg-mobile-mvp.conf
/usr/share/applications/ghelper-xg-mvp.desktop
~/.config/autostart/ghelper-xg-mvp.desktop
~/.config/autostart/nvidia-settings-user.desktop
```

The systemd unit is enabled explicitly by the installer; D-Bus activation is not
used. The daemon receives only the capabilities and device visibility required
by the current live transition implementation. It does not provide a generic
root command or arbitrary sysfs path API.

## Boot and desktop requirements

The tested GV301QH requires its XG root port to remain awake during a live
transition. The installed udev rule matches the complete AMD/ASUS PCI identity
and sets only that port's runtime `power/control` to `on`. It does not disable
bridge power management globally. Upgrades remove `pcie_port_pm=off` only when
an earlier version of this installer added it; a preexisting user fallback is
left untouched.

The Mutter udev rule ignores only NVIDIA IDs `10de:1f9d` and `10de:249c`. This
keeps GNOME on the AMD iGPU while individual applications can still use NVIDIA
through PRIME render offload. GDM autologin is not required and is not enabled.

The GNOME portal is restricted to Mesa's EGL implementation. Without that
service-local override, GLVND opens the NVIDIA render node whenever
`nvidia_drm` appears and prevents a later live disconnect even after games have
closed. NVIDIA settings autostart is suppressed for the same reason; G-Helper
owns the explicit driver transition.

The user-device rule grants the active local session access only to the
GV301QH `asus-nb-wmi` hotkey events and ASUS N-KEY `0b05:19b6` input/HID
functions. It deliberately excludes the XG Mobile ITE `0b05:1970` controller,
which remains owned by the privileged daemon.

When the XG is active, the suspend hook sends the same HID sleep/wake sequence
used by upstream Windows G-Helper and temporarily unbinds `AMDI0005:00` from
`amd_pmc` across s2idle. It then rebinds PMC before returning to userspace. This
preserves the GNOME session and the RTX on the validated GV301QH, but deliberately
does not reach deep S0i3, so long suspended battery life remains limited.

## Current trust level

This is a locally reproducible, unsigned MVP, not a signed distribution package.
The canonical build records source/environment provenance and emits GUI plus
daemon in one artifact tree. RPM ownership, signing, update delivery, broader
hardware support, and additional daemon hardening remain post-MVP work.

On 2026-08-21 the installed GV301QH completed a live Standard -> Eco -> Standard
cycle. Eco changed `dgpu_disable` from 0 to 1 and removed `10de:1f9d` from PCI;
Standard restored the internal device and bound NVIDIA 610.57.04. With the XG
subsequently reconnected but left inactive, firmware reported 1/0 and its
`0b05:1970` HID appeared without disturbing the internal GPU. No task entered
uninterruptible sleep. The active `andres` session also completed an authorized
no-op Standard request without a password prompt. The implementation passed
149/149 C# scenarios, 93/93 boot scenarios, 12/12 audio integration tests, and
the two-build Native AOT reproducibility check.
