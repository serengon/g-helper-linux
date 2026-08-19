# XG Mobile MVP privilege boundary

The installed application runs as the desktop user. It does not invoke `sudo`,
`pkexec`, a shell, or `systemctl`. Privileged XG Mobile switching is isolated in
`ghelperd`, a separate root service on the system D-Bus.

## D-Bus contract and authorization

- Bus name: `org.ghelper.Daemon1`
- Object: `/org/ghelper/Daemon1`
- Interface: `org.ghelper.Daemon1`
- Enabled mutation: `enable-xg-mode` / `disable-xg-mode`
- Polkit action: `org.ghelper.daemon.set-xg-mode`

The daemon takes the unique D-Bus sender from the received message and resolves
its UID/PID through the bus. Polkit authorizes that unique bus name as the active
local session. Caller-provided identity is never accepted. Other declared future
operations still return unsupported.

The XG executor is intentionally model-specific. It requires a GV301QH, the ASUS
`egpu_connected` and `egpu_enable` attributes, exactly one visible NVIDIA GPU,
and no process retaining `/dev/nvidia*`. It never kills an application. It owns
NVIDIA module release, HDMI-audio unbind, ASUS WMI transition, XG HID reports,
PCI rescan, and final endpoint verification.

## Installed system assets

`scripts/ghelper-xg-mvp.sh install DIST_DIR USER` installs:

```text
/opt/ghelper-xg-mvp/
/usr/libexec/ghelper/ghelperd
/etc/systemd/system/ghelperd.service
/etc/dbus-1/system.d/org.ghelper.Daemon1.conf
/usr/share/polkit-1/actions/org.ghelper.daemon.policy
/etc/udev/rules.d/61-mutter-ignore-x13-nvidia.rules
/etc/ghelper/xg-mobile-mvp.conf
/usr/share/applications/ghelper-xg-mvp.desktop
~/.config/autostart/ghelper-xg-mvp.desktop
```

The systemd unit is enabled explicitly by the installer; D-Bus activation is not
used. The daemon receives only the capabilities and device visibility required
by the current live transition implementation. It does not provide a generic
root command or arbitrary sysfs path API.

## Boot and desktop requirements

The tested GV301QH requires `pcie_port_pm=off`; without it the firmware can leave
the PCIe transition stuck. The installer adds the token with `grubby` only when
missing, records ownership of that change, and requests a reboot. Uninstall only
removes a token that the installer originally added.

The Mutter udev rule ignores only NVIDIA IDs `10de:1f9d` and `10de:249c`. This
keeps GNOME on the AMD iGPU while individual applications can still use NVIDIA
through PRIME render offload. GDM autologin is not required and is not enabled.

## Current trust level

This is a locally reproducible, unsigned MVP, not a signed distribution package.
The canonical build records source/environment provenance and emits GUI plus
daemon in one artifact tree. RPM ownership, signing, update delivery, broader
hardware support, and additional daemon hardening remain post-MVP work.
