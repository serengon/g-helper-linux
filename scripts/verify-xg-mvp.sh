#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_DIR"

STATIC_ONLY=0
DIST_DIR=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --static-only) STATIC_ONLY=1; shift ;;
        --dist)
            [[ $# -ge 2 ]] || { echo "--dist requires a directory" >&2; exit 2; }
            DIST_DIR="$2"
            shift 2
            ;;
        *) echo "Usage: $0 [--static-only] [--dist /absolute/build]" >&2; exit 2 ;;
    esac
done

fail() {
    echo "ERROR: $*" >&2
    exit 1
}

bash -n build.sh scripts/build-container.sh scripts/ghelper-xg-mvp.sh \
    packaging/system-sleep/ghelper-xg-suspend
python3 - <<'PY'
from pathlib import Path
compile(
    Path("packaging/libexec/ghelper-xg-hid-power.py").read_text(),
    "packaging/libexec/ghelper-xg-hid-power.py",
    "exec",
)
PY
git diff --check

[[ -x scripts/ghelper-xg-mvp.sh ]] || fail "MVP installer is not executable"
[[ -f packaging/applications/ghelper-xg-mvp.desktop ]] \
    || fail "installed application launcher asset missing"
[[ -f packaging/autostart/ghelper-xg-mvp.desktop ]] || fail "MVP autostart asset missing"
[[ -f packaging/autostart/nvidia-settings-user.desktop ]] \
    || fail "NVIDIA settings autostart override missing"
[[ -f packaging/system-sleep/ghelper-xg-suspend ]] \
    || fail "XG suspend hook missing"
[[ -f packaging/libexec/ghelper-xg-hid-power.py ]] \
    || fail "XG HID power helper missing"
[[ ! -e packaging/autostart/ghelper-poc-live-xg.desktop ]] || fail "legacy POC autostart asset remains"
[[ ! -e scripts/install-xg-live-poc.sh && ! -e scripts/configure-xg-poc-login.sh ]] \
    || fail "legacy POC installer remains"

rg -q 'InstalledMvpMarkerPath = "/etc/ghelper/xg-mobile-mvp\.conf"' \
    src/Helpers/RuntimeMode.cs || fail "installed runtime marker is missing"
rg -q 'StartupIntent\.InstalledMvp' src/Helpers/RuntimeMode.cs \
    || fail "normal installed runtime mode is missing"
[[ -f packaging/udev/80-ghelper-xg-root-port-pm.rules ]] \
    || fail "targeted XG root-port PM rule missing"
[[ -f packaging/udev/62-ghelper-x13-user-devices.rules ]] \
    || fail "X13 user-device access rule missing"
rg -q '0b05.*19b6.*uaccess|0B05.*19B6.*uaccess' \
    packaging/udev/62-ghelper-x13-user-devices.rules \
    || fail "X13 keyboard HID access is not model-specific"
! rg -qi '1970.*uaccess' packaging/udev/62-ghelper-x13-user-devices.rules \
    || fail "unprivileged XG Mobile HID access was enabled"
rg -q 'ATTR\{vendor\}=="0x1022".*ATTR\{device\}=="0x1633".*ATTR\{subsystem_vendor\}=="0x1043".*ATTR\{subsystem_device\}=="0x1662"' \
    packaging/udev/80-ghelper-xg-root-port-pm.rules \
    || fail "targeted XG root-port PM identity is incomplete"
! rg -q 'KERNEL=="0000:00:01.1"' \
    packaging/udev/80-ghelper-xg-root-port-pm.rules \
    || fail "targeted XG root-port PM rule depends on a fixed bus address"
rg -q 'pcie_port_pm=off' scripts/ghelper-xg-mvp.sh README.md \
    || fail "legacy kernel fallback is not managed/documented"
rg -q 'mutter-device-ignore' packaging/udev/61-mutter-ignore-x13-nvidia.rules \
    || fail "Mutter release rule is missing"
rg -q '__EGL_VENDOR_LIBRARY_FILENAMES=.*/50_mesa.json' \
    packaging/systemd/user/xdg-desktop-portal-gnome.service.d/50-ghelper-x13-integrated-gpu.conf \
    || fail "GNOME portal is not pinned to Mesa/AMD"
rg -q 'NVreg_EnableS0ixPowerManagement=1.*NVreg_UseKernelSuspendNotifiers=0' \
    packaging/modprobe/ghelper-xg-suspend.conf \
    || fail "validated NVIDIA suspend configuration is missing"
rg -q '5E-E4-01|0xE4, 0x01' \
    packaging/libexec/ghelper-xg-hid-power.py \
    || fail "XG HID sleep report is missing"
rg -q 'amd_pmc.*AMDI0005:00|driver="/sys/bus/platform/drivers/amd_pmc"' \
    packaging/system-sleep/ghelper-xg-suspend \
    || fail "amd_pmc suspend workaround is missing"
rg -q 'pocReadOnlyBanner.IsVisible = !RuntimeMode.IsInstalledMvp' \
    src/UI/Views/MainWindow.axaml.cs \
    || fail "installed UI still exposes the development banner"
rg -q 'waiting for daemon settle' src/UI/Views/MainWindow.axaml.cs \
    || fail "XG enable flow does not wait for daemon settlement"
rg -q 'system/ghelperd' build.sh scripts/ghelper-xg-mvp.sh \
    || fail "GUI and daemon are not a single build/install artifact"
rg -q '^Exec=/opt/ghelper-xg-mvp/ghelper --minimized$' \
    packaging/autostart/ghelper-xg-mvp.desktop \
    || fail "autostart does not launch the canonical installed GUI minimized"
rg -q '^Exec=/opt/ghelper-xg-mvp/ghelper$' \
    packaging/applications/ghelper-xg-mvp.desktop \
    || fail "application launcher does not open the canonical installed GUI"
rg -q 'if \(RuntimeMode\.IsInstalledMvp\)' src/App.axaml.cs \
    || fail "installed runtime does not own a session tray"
rg -q 'SetupTrayIcon\(desktop\)' src/App.axaml.cs \
    || fail "installed runtime does not create a tray icon"
rg -q 'CommandIpc\.TrySend\("show-main"\)' src/Program.cs \
    && rg -q 'command == "show-main"' src/App.axaml.cs \
    || fail "desktop launcher cannot wake the installed tray instance"
rg -q 'ToggleXgMobileFromTrayAsync' src/App.axaml.cs src/UI/Views/MainWindow.axaml.cs \
    || fail "installed tray does not expose the authoritative XG transition"
! rg -q '/home/andres|--poc-functional' packaging scripts/ghelper-xg-mvp.sh \
    || fail "installed assets contain a POC flag or hard-coded user path"
! rg -q 'AutomaticLoginEnable=True|AutomaticLogin=andres' packaging \
    || fail "MVP packaging enables GDM autologin"
rg -q '^restore_poc_autologin\(\)' scripts/ghelper-xg-mvp.sh \
    || fail "POC autologin migration is missing"

for operation in install status uninstall; do
    rg -q "^[[:space:]]*${operation}\)" scripts/ghelper-xg-mvp.sh \
        || fail "installer omits $operation command"
done

if [[ -n "$DIST_DIR" ]]; then
    [[ "$DIST_DIR" == /* && -d "$DIST_DIR" && ! -L "$DIST_DIR" ]] \
        || fail "--dist is not an absolute non-symlink directory"
    [[ -x "$DIST_DIR/ghelper" && -x "$DIST_DIR/system/ghelperd" ]] \
        || fail "build lacks GUI or daemon executable"
    set +e
    "$DIST_DIR/ghelper" >/dev/null 2>&1
    plain_rc=$?
    set -e
    [[ "$plain_rc" == "64" ]] \
        || fail "uninstalled artifact normal launch did not fail closed (rc=$plain_rc)"
    "$DIST_DIR/ghelper" --poc-smoke | grep -q '^POC_SMOKE_OK$' \
        || fail "read-only smoke failed"
fi

echo "XG Mobile MVP static acceptance passed"
[[ "$STATIC_ONLY" == "1" ]] && exit 0

"$SCRIPT_DIR/verify-phase2.sh" --static-only
exec "$SCRIPT_DIR/verify-phase1.sh"
