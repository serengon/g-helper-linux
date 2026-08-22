#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

MVP_MODE="xg-mobile-mvp-v1"
MODEL_TOKEN="GV301QH"
KERNEL_ARG="pcie_port_pm=off"
INSTALL_DIR="/opt/ghelper-xg-mvp"
DAEMON_DIR="/usr/libexec/ghelper"
DAEMON_PATH="$DAEMON_DIR/ghelperd"
STATE_DIR="/var/lib/ghelper-xg-mvp"
STATE_FILE="$STATE_DIR/state"
MARKER_DIR="/etc/ghelper"
MARKER_FILE="$MARKER_DIR/xg-mobile-mvp.conf"
UNIT_FILE="/etc/systemd/system/ghelperd.service"
DBUS_FILE="/etc/dbus-1/system.d/org.ghelper.Daemon1.conf"
POLKIT_FILE="/usr/share/polkit-1/actions/org.ghelper.daemon.policy"
UDEV_FILE="/etc/udev/rules.d/61-mutter-ignore-x13-nvidia.rules"
USER_DEVICE_RULE="/etc/udev/rules.d/62-ghelper-x13-user-devices.rules"
ROOT_PORT_PM_RULE="/etc/udev/rules.d/80-ghelper-xg-root-port-pm.rules"
PORTAL_DROPIN_DIR="/etc/systemd/user/xdg-desktop-portal-gnome.service.d"
PORTAL_DROPIN_FILE="$PORTAL_DROPIN_DIR/50-ghelper-x13-integrated-gpu.conf"
SUSPEND_HOOK_FILE="/usr/lib/systemd/system-sleep/ghelper-xg-suspend"
HID_POWER_FILE="$DAEMON_DIR/ghelper-xg-hid-power.py"
NVIDIA_SUSPEND_CONFIG="/etc/modprobe.d/ghelper-xg-suspend.conf"
APPLICATION_NAME="ghelper-xg-mvp.desktop"
DESKTOP_FILE="/usr/share/applications/$APPLICATION_NAME"
DOC_DIR="/usr/share/doc/ghelper-xg-mvp"
DOC_FILE="$DOC_DIR/privilege-boundary.md"
AUTOSTART_NAME="ghelper-xg-mvp.desktop"
NVIDIA_AUTOSTART_NAME="nvidia-settings-user.desktop"
LEGACY_AUTOSTART_NAME="ghelper-poc-live-xg.desktop"

usage() {
    cat <<EOF
Usage:
  sudo $0 install DIST_DIR USER
  $0 status [USER]
  sudo $0 uninstall [USER]

DIST_DIR must be a canonical G-Helper build containing:
  ghelper
  system/ghelperd

The MVP currently supports only ASUS ROG Flow X13 GV301QH on GNOME/Mutter.
Installation keeps only the XG root port awake. A legacy $KERNEL_ARG that this
installer previously added is removed on migration.
EOF
}

die() {
    echo "ERROR: $*" >&2
    exit 1
}

require_root() {
    [[ "$(id -u)" == "0" ]] || die "this operation must run as root"
}

command_required() {
    command -v "$1" >/dev/null 2>&1 || die "required command not found: $1"
}

user_systemctl() {
    runuser -u "$TARGET_USER" -- \
        env "XDG_RUNTIME_DIR=/run/user/$TARGET_UID" systemctl --user "$@"
}

state_value() {
    local key="$1"
    [[ -f "$STATE_FILE" && ! -L "$STATE_FILE" ]] || return 1
    awk -F= -v wanted="$key" '
        $1 == wanted { sub(/^[^=]*=/, ""); print; found++ }
        END { if (found != 1) exit 1 }
    ' "$STATE_FILE"
}

resolve_user() {
    local requested="${1:-}"
    if [[ -z "$requested" && -f "$STATE_FILE" ]]; then
        requested="$(state_value user || true)"
    fi
    [[ "$requested" =~ ^[a-z_][a-z0-9_-]*[$]?$ ]] \
        || die "a valid target USER is required"
    local passwd
    passwd="$(getent passwd "$requested")" || die "unknown user: $requested"
    TARGET_USER="$requested"
    TARGET_UID="$(cut -d: -f3 <<<"$passwd")"
    TARGET_GID="$(cut -d: -f4 <<<"$passwd")"
    TARGET_HOME="$(cut -d: -f6 <<<"$passwd")"
    [[ "$TARGET_HOME" == /* && -d "$TARGET_HOME" && ! -L "$TARGET_HOME" ]] \
        || die "unsafe home for $TARGET_USER: $TARGET_HOME"
}

product_name() {
    cat /sys/class/dmi/id/product_name 2>/dev/null || true
}

preflight_host() {
    local product
    product="$(product_name)"
    [[ "$product" == *"$MODEL_TOKEN"* ]] \
        || die "unsupported model: ${product:-unknown}; expected $MODEL_TOKEN"
    [[ -e /sys/devices/platform/asus-nb-wmi/egpu_connected \
       && -e /sys/devices/platform/asus-nb-wmi/egpu_enable ]] \
        || die "ASUS XG Mobile firmware attributes are unavailable"
    command -v gnome-shell >/dev/null 2>&1 \
        || die "GNOME/Mutter is required by this MVP"
    [[ -f /usr/share/glvnd/egl_vendor.d/50_mesa.json ]] \
        || die "Mesa's GLVND EGL vendor file is required"
    find_xg_root_port >/dev/null \
        || die "the unique GV301QH XG root port is unavailable"
    [[ -f /usr/lib/systemd/system/nvidia-suspend.service \
       && -f /usr/lib/systemd/system/nvidia-resume.service ]] \
        || die "xorg-x11-drv-nvidia-power is required for XG suspend/resume"
}

kernel_arg_running() {
    [[ -r /proc/cmdline ]] || return 1
    grep -Eq "(^|[[:space:]])${KERNEL_ARG}([[:space:]]|$)" /proc/cmdline
}

kernel_arg_in_all_entries() {
    command -v grubby >/dev/null 2>&1 || return 1
    local args found=0
    while IFS= read -r args; do
        found=1
        case " $args " in
            *" $KERNEL_ARG "*) ;;
            *) return 1 ;;
        esac
    done < <(grubby --info=ALL 2>/dev/null \
        | sed -n 's/^args="\(.*\)"$/\1/p')
    [[ "$found" == "1" ]]
}

refresh_initramfs() {
    if command -v update-initramfs >/dev/null 2>&1; then
        update-initramfs -u -k "$(uname -r)"
    elif command -v dracut >/dev/null 2>&1; then
        dracut --force "/boot/initramfs-$(uname -r).img" "$(uname -r)"
    else
        die "neither update-initramfs nor dracut is available"
    fi
}

remove_legacy_kernel_arg() {
    if command -v grubby >/dev/null 2>&1; then
        grubby --update-kernel=ALL --remove-args="$KERNEL_ARG"
    fi
    remove_kernel_cmdline_token
}

gdm_config_path() {
    if [[ -f /etc/gdm3/custom.conf ]]; then
        printf '%s\n' /etc/gdm3/custom.conf
    else
        printf '%s\n' /etc/gdm/custom.conf
    fi
}

kernel_arg_persisted() {
    if [[ -r /etc/kernel/cmdline ]]; then
        grep -Eq "(^|[[:space:]])${KERNEL_ARG}([[:space:]]|$)" /etc/kernel/cmdline
        return
    fi
    kernel_arg_in_all_entries
}

remove_kernel_cmdline_token() {
    local file=/etc/kernel/cmdline token stage
    [[ -f "$file" && ! -L "$file" ]] || return 0
    stage="$(mktemp /etc/kernel/.ghelper-cmdline.XXXXXX)"
    for token in $(cat "$file"); do
        [[ "$token" == "$KERNEL_ARG" ]] || printf '%s ' "$token" >>"$stage"
    done
    sed -i 's/[[:space:]]*$//' "$stage"
    printf '\n' >>"$stage"
    chown root:root "$stage"
    chmod 0644 "$stage"
    mv -f -- "$stage" "$file"
}

find_xg_root_port() {
    local path found="" count=0
    for path in /sys/bus/pci/devices/*; do
        [[ -f "$path/class" && -f "$path/vendor" && -f "$path/device" \
           && -f "$path/subsystem_vendor" && -f "$path/subsystem_device" \
           && -f "$path/power/control" ]] || continue
        [[ "$(cat "$path/class")" == "0x060400" \
           && "$(cat "$path/vendor")" == "0x1022" \
           && "$(cat "$path/device")" == "0x1633" \
           && "$(cat "$path/subsystem_vendor")" == "0x1043" \
           && "$(cat "$path/subsystem_device")" == "0x1662" ]] || continue
        found="$path"
        count=$((count + 1))
    done
    [[ "$count" == "1" ]] || return 1
    printf '%s\n' "$found"
}

root_port_pm_active() {
    local root_port
    root_port="$(find_xg_root_port)" || return 1
    [[ "$(cat "$root_port/power/control" 2>/dev/null)" == "on" ]]
}

validate_asset() {
    local path="$1"
    [[ -f "$path" && ! -L "$path" ]] || die "missing or unsafe asset: $path"
}

validate_dist() {
    local requested="$1"
    [[ "$requested" == /* && -d "$requested" && ! -L "$requested" ]] \
        || die "DIST_DIR must be an absolute, non-symlink directory"
    DIST_DIR_REAL="$(realpath -e -- "$requested")"
    [[ "$DIST_DIR_REAL" == "$requested" ]] || die "DIST_DIR must be canonical"
    [[ -f "$DIST_DIR_REAL/ghelper" && -x "$DIST_DIR_REAL/ghelper" \
       && ! -L "$DIST_DIR_REAL/ghelper" ]] || die "DIST_DIR/ghelper is missing or unsafe"
    [[ -f "$DIST_DIR_REAL/system/ghelperd" \
       && -x "$DIST_DIR_REAL/system/ghelperd" \
       && ! -L "$DIST_DIR_REAL/system/ghelperd" ]] \
        || die "DIST_DIR/system/ghelperd is missing or unsafe"
    ! find -P "$DIST_DIR_REAL" -type l -print -quit | grep -q . \
        || die "DIST_DIR contains symlinks"
    ! find -P "$DIST_DIR_REAL" ! -type f ! -type d -print -quit | grep -q . \
        || die "DIST_DIR contains special files"
}

install_gui_tree() {
    local stage backup entry name
    stage="$(mktemp -d /opt/.ghelper-xg-mvp.XXXXXX)"
    chmod 0755 "$stage"
    while IFS= read -r -d '' entry; do
        name="$(basename -- "$entry")"
        [[ "$name" == "system" ]] && continue
        cp -a -- "$entry" "$stage/"
    done < <(find -P "$DIST_DIR_REAL" -mindepth 1 -maxdepth 1 -print0)
    chown -R root:root "$stage"
    find -P "$stage" -type d -exec chmod 0755 {} +
    find -P "$stage" -type f -exec chmod 0644 {} +
    chmod 0755 "$stage/ghelper"
    find -P "$stage" -type f -name '*.so' -exec chmod 0755 {} +
    [[ ! -f "$stage/createdump" ]] || chmod 0755 "$stage/createdump"

    backup="/opt/.ghelper-xg-mvp.previous.$$"
    [[ ! -e "$backup" && ! -L "$backup" ]] || die "occupied GUI backup path"
    if [[ -e "$INSTALL_DIR" ]]; then
        [[ -d "$INSTALL_DIR" && ! -L "$INSTALL_DIR" ]] \
            || die "existing install path is unsafe"
        mv -- "$INSTALL_DIR" "$backup"
    fi
    if ! mv -- "$stage" "$INSTALL_DIR"; then
        [[ ! -e "$backup" ]] || mv -- "$backup" "$INSTALL_DIR"
        die "could not publish GUI installation"
    fi
    if [[ -d "$backup" ]]; then
        find -P "$backup" -depth -delete
    fi
}

install_user_files() {
    local config_parent old_config new_config autostart_dir legacy
    config_parent="$TARGET_HOME/.config"
    autostart_dir="$config_parent/autostart"
    install -d -o "$TARGET_UID" -g "$TARGET_GID" -m 0755 "$config_parent" "$autostart_dir"
    install -o "$TARGET_UID" -g "$TARGET_GID" -m 0644 \
        "$REPO_DIR/packaging/autostart/$AUTOSTART_NAME" \
        "$autostart_dir/$AUTOSTART_NAME"
    install -o "$TARGET_UID" -g "$TARGET_GID" -m 0644 \
        "$REPO_DIR/packaging/autostart/$NVIDIA_AUTOSTART_NAME" \
        "$autostart_dir/$NVIDIA_AUTOSTART_NAME"

    old_config="$config_parent/ghelper-poc/ghelper"
    new_config="$config_parent/ghelper"
    if [[ -d "$old_config" && ! -L "$old_config" && ! -e "$new_config" ]]; then
        cp -a -- "$old_config" "$new_config"
        chown -R "$TARGET_UID:$TARGET_GID" "$new_config"
        echo "Migrated functional POC configuration to $new_config"
    fi

    legacy="$autostart_dir/$LEGACY_AUTOSTART_NAME"
    if [[ -f "$legacy" && ! -L "$legacy" ]]; then
        install -o root -g root -m 0600 "$legacy" "$STATE_DIR/$LEGACY_AUTOSTART_NAME.backup"
        rm -f -- "$legacy"
    fi
}

restore_poc_autologin() {
    local gdm backup
    gdm="$(gdm_config_path)"
    backup="${gdm}.ghelper-poc-backup"
    GDM_AUTLOGIN_RESTORED=0
    if [[ -f "$gdm" && ! -L "$gdm" \
       && -f "$backup" && ! -L "$backup" ]] \
       && grep -Eq '^[[:space:]]*AutomaticLoginEnable=True[[:space:]]*$' "$gdm" \
       && grep -Eq "^[[:space:]]*AutomaticLogin=${TARGET_USER}[[:space:]]*$" "$gdm"; then
        install -o root -g root -m 0600 "$gdm" "$STATE_DIR/gdm-custom.conf.poc.backup"
        install -o root -g root -m 0644 "$backup" "$gdm"
        GDM_AUTLOGIN_RESTORED=1
        echo "Restored the pre-POC GDM configuration; autologin is no longer required."
    fi
}

write_state() {
    local stage
    stage="$(mktemp "$STATE_DIR/.state.XXXXXX")"
    {
        printf 'format=%s\n' "$MVP_MODE"
        printf 'user=%s\n' "$TARGET_USER"
        printf 'home=%s\n' "$TARGET_HOME"
        printf 'kernel_arg_added=%s\n' "$KERNEL_ARG_ADDED"
        printf 'legacy_kernel_arg_preexisting=%s\n' "$LEGACY_KERNEL_ARG_PREEXISTING"
        printf 'targeted_root_port_pm=1\n'
        printf 'gdm_autologin_restored=%s\n' "$GDM_AUTLOGIN_RESTORED"
        printf 'gui_sha256=%s\n' "$(sha256sum "$DIST_DIR_REAL/ghelper" | awk '{print $1}')"
        printf 'daemon_sha256=%s\n' "$(sha256sum "$DIST_DIR_REAL/system/ghelperd" | awk '{print $1}')"
    } >"$stage"
    chmod 0600 "$stage"
    chown root:root "$stage"
    mv -f -- "$stage" "$STATE_FILE"
}

install_mvp() {
    require_root
    [[ $# == 2 ]] || { usage >&2; exit 64; }
    for command in getent realpath systemctl busctl udevadm sha256sum python3 runuser; do
        command_required "$command"
    done
    resolve_user "$2"
    preflight_host
    validate_dist "$1"
    for asset in \
        "$REPO_DIR/packaging/systemd/ghelperd.service" \
        "$REPO_DIR/packaging/dbus/org.ghelper.Daemon1.conf" \
        "$REPO_DIR/packaging/polkit/org.ghelper.daemon.policy" \
        "$REPO_DIR/packaging/udev/61-mutter-ignore-x13-nvidia.rules" \
        "$REPO_DIR/packaging/udev/62-ghelper-x13-user-devices.rules" \
        "$REPO_DIR/packaging/udev/80-ghelper-xg-root-port-pm.rules" \
        "$REPO_DIR/packaging/systemd/user/xdg-desktop-portal-gnome.service.d/50-ghelper-x13-integrated-gpu.conf" \
        "$REPO_DIR/packaging/system-sleep/ghelper-xg-suspend" \
        "$REPO_DIR/packaging/libexec/ghelper-xg-hid-power.py" \
        "$REPO_DIR/packaging/modprobe/ghelper-xg-suspend.conf" \
        "$REPO_DIR/packaging/applications/$APPLICATION_NAME" \
        "$REPO_DIR/packaging/autostart/$AUTOSTART_NAME" \
        "$REPO_DIR/packaging/autostart/$NVIDIA_AUTOSTART_NAME" \
        "$REPO_DIR/docs/privilege-boundary.md"; do
        validate_asset "$asset"
    done

    install -d -o root -g root -m 0700 "$STATE_DIR"
    if [[ -f "$STATE_FILE" ]]; then
        [[ "$(state_value user)" == "$TARGET_USER" ]] \
            || die "existing MVP belongs to a different user"
        KERNEL_ARG_ADDED="$(state_value kernel_arg_added)"
        LEGACY_KERNEL_ARG_PREEXISTING="$(state_value legacy_kernel_arg_preexisting || echo 0)"
    elif kernel_arg_persisted; then
        KERNEL_ARG_ADDED=0
        LEGACY_KERNEL_ARG_PREEXISTING=1
    else
        KERNEL_ARG_ADDED=0
        LEGACY_KERNEL_ARG_PREEXISTING=0
    fi

    install_gui_tree
    install -d -o root -g root -m 0755 "$DAEMON_DIR" "$MARKER_DIR" \
        "$PORTAL_DROPIN_DIR" /usr/lib/systemd/system-sleep /etc/modprobe.d
    install -o root -g root -m 0755 "$DIST_DIR_REAL/system/ghelperd" "$DAEMON_PATH"
    install -o root -g root -m 0644 "$REPO_DIR/packaging/systemd/ghelperd.service" "$UNIT_FILE"
    install -o root -g root -m 0644 "$REPO_DIR/packaging/dbus/org.ghelper.Daemon1.conf" "$DBUS_FILE"
    install -o root -g root -m 0644 "$REPO_DIR/packaging/polkit/org.ghelper.daemon.policy" "$POLKIT_FILE"
    install -o root -g root -m 0644 "$REPO_DIR/packaging/udev/61-mutter-ignore-x13-nvidia.rules" "$UDEV_FILE"
    install -o root -g root -m 0644 \
        "$REPO_DIR/packaging/udev/62-ghelper-x13-user-devices.rules" \
        "$USER_DEVICE_RULE"
    install -o root -g root -m 0644 \
        "$REPO_DIR/packaging/udev/80-ghelper-xg-root-port-pm.rules" \
        "$ROOT_PORT_PM_RULE"
    install -o root -g root -m 0644 \
        "$REPO_DIR/packaging/systemd/user/xdg-desktop-portal-gnome.service.d/50-ghelper-x13-integrated-gpu.conf" \
        "$PORTAL_DROPIN_FILE"
    install -o root -g root -m 0755 \
        "$REPO_DIR/packaging/system-sleep/ghelper-xg-suspend" \
        "$SUSPEND_HOOK_FILE"
    install -o root -g root -m 0755 \
        "$REPO_DIR/packaging/libexec/ghelper-xg-hid-power.py" \
        "$HID_POWER_FILE"
    install -o root -g root -m 0644 \
        "$REPO_DIR/packaging/modprobe/ghelper-xg-suspend.conf" \
        "$NVIDIA_SUSPEND_CONFIG"
    install -o root -g root -m 0644 \
        "$REPO_DIR/packaging/applications/$APPLICATION_NAME" "$DESKTOP_FILE"
    install -d -o root -g root -m 0755 "$DOC_DIR"
    install -o root -g root -m 0644 "$REPO_DIR/docs/privilege-boundary.md" "$DOC_FILE"
    {
        printf 'mode=%s\n' "$MVP_MODE"
        printf 'model=%s\n' "$MODEL_TOKEN"
    } >"$MARKER_FILE"
    chown root:root "$MARKER_FILE"
    chmod 0644 "$MARKER_FILE"

    install_user_files
    restore_poc_autologin
    write_state

    systemctl daemon-reload
    systemctl mask nvidia-powerd.service
    systemctl enable nvidia-suspend.service nvidia-resume.service \
        nvidia-hibernate.service nvidia-suspend-then-hibernate.service
    refresh_initramfs
    if systemctl --quiet is-active "user@${TARGET_UID}.service"; then
        user_systemctl daemon-reload
        user_systemctl \
            try-restart xdg-desktop-portal-gnome.service xdg-desktop-portal.service
    fi
    busctl call org.freedesktop.DBus /org/freedesktop/DBus \
        org.freedesktop.DBus ReloadConfig >/dev/null
    udevadm control --reload
    ROOT_PORT_PATH="$(find_xg_root_port)" \
        || die "the unique GV301QH XG root port disappeared"
    udevadm trigger --subsystem-match=pci \
        --sysname-match="$(basename "$ROOT_PORT_PATH")" --action=change
    udevadm settle --timeout=20
    root_port_pm_active \
        || die "the targeted XG root-port runtime PM policy did not apply"
    udevadm trigger --subsystem-match=input --action=change
    udevadm trigger --subsystem-match=hidraw --action=change
    udevadm settle --timeout=20
    udevadm trigger --subsystem-match=drm --action=add

    if [[ "$KERNEL_ARG_ADDED" == "1" ]]; then
        remove_legacy_kernel_arg
        KERNEL_ARG_ADDED=0
        write_state
    fi
    systemctl enable --now ghelperd.service
    systemctl restart ghelperd.service
    systemctl --quiet is-active ghelperd.service \
        || die "ghelperd did not become active"

    echo
    echo "G-Helper XG Mobile MVP installed for $TARGET_USER."
    if kernel_arg_running && ! kernel_arg_persisted; then
        echo "REBOOT REQUIRED: retire the legacy $KERNEL_ARG from the running kernel."
    elif kernel_arg_running; then
        echo "Legacy fallback remains user-managed: $KERNEL_ARG."
    else
        echo "Targeted XG root-port runtime PM policy is active."
    fi
    echo "Log out/in once if Mutter has not seen the udev rule."
}

check_file() {
    local label="$1" path="$2" required_mode="${3:-}"
    if [[ -f "$path" && ! -L "$path" \
       && ( -z "$required_mode" || "$(stat -c %a "$path")" == "$required_mode" ) ]]; then
        printf 'ok      %s\n' "$label"
        return 0
    fi
    printf 'missing %s (%s)\n' "$label" "$path"
    return 1
}

check_executable() {
    local label="$1" path="$2"
    if [[ -f "$path" && ! -L "$path" && -x "$path" ]]; then
        printf 'ok      %s\n' "$label"
        return 0
    fi
    printf 'missing %s (%s)\n' "$label" "$path"
    return 1
}

mutter_ignore_active() {
    local card found=0 tags
    for card in /sys/class/drm/card[0-9]*; do
        [[ -e "$card/device/vendor" ]] || continue
        [[ "$(cat "$card/device/vendor" 2>/dev/null)" == "0x10de" ]] || continue
        found=1
        tags="$(udevadm info --query=property --path="$card" 2>/dev/null \
            | sed -n 's/^CURRENT_TAGS=//p')"
        [[ ":$tags:" == *":mutter-device-ignore:"* ]] || return 1
    done
    [[ "$found" == "1" ]] || return 2
}

status_mvp() {
    [[ $# -le 1 ]] || { usage >&2; exit 64; }
    for command in getent systemctl udevadm; do command_required "$command"; done
    resolve_user "${1:-}"
    local failures=0 connected enabled nvidia
    printf 'G-Helper XG Mobile MVP status\n'
    printf 'model: %s\n' "$(product_name)"
    [[ "$(product_name)" == *"$MODEL_TOKEN"* ]] \
        && printf 'ok      supported model\n' \
        || { printf 'missing supported model %s\n' "$MODEL_TOKEN"; failures=$((failures + 1)); }
    check_file 'MVP marker' "$MARKER_FILE" || failures=$((failures + 1))
    check_executable 'GUI executable' "$INSTALL_DIR/ghelper" || failures=$((failures + 1))
    check_executable 'daemon executable' "$DAEMON_PATH" || failures=$((failures + 1))
    check_file 'Mutter udev rule' "$UDEV_FILE" || failures=$((failures + 1))
    check_file 'X13 user-device access rule' "$USER_DEVICE_RULE" \
        || failures=$((failures + 1))
    check_file 'targeted XG root-port PM rule' "$ROOT_PORT_PM_RULE" \
        || failures=$((failures + 1))
    if root_port_pm_active; then
        printf 'ok      targeted XG root-port runtime PM active\n'
    else
        printf 'missing targeted XG root-port runtime PM active\n'
        failures=$((failures + 1))
    fi
    check_file 'GNOME portal integrated-GPU override' "$PORTAL_DROPIN_FILE" \
        || failures=$((failures + 1))
    check_executable 'XG suspend hook' "$SUSPEND_HOOK_FILE" \
        || failures=$((failures + 1))
    check_executable 'XG HID power helper' "$HID_POWER_FILE" \
        || failures=$((failures + 1))
    check_file 'NVIDIA XG suspend configuration' "$NVIDIA_SUSPEND_CONFIG" \
        || failures=$((failures + 1))
    check_file 'D-Bus policy' "$DBUS_FILE" || failures=$((failures + 1))
    check_file 'polkit policy' "$POLKIT_FILE" || failures=$((failures + 1))
    check_file 'installed MVP documentation' "$DOC_FILE" || failures=$((failures + 1))
    check_file 'user autostart' "$TARGET_HOME/.config/autostart/$AUTOSTART_NAME" \
        || failures=$((failures + 1))
    check_file 'NVIDIA settings autostart override' \
        "$TARGET_HOME/.config/autostart/$NVIDIA_AUTOSTART_NAME" \
        || failures=$((failures + 1))
    if systemctl --quiet is-active ghelperd.service; then
        printf 'ok      ghelperd active\n'
    else
        printf 'missing ghelperd active\n'
        failures=$((failures + 1))
    fi
    kernel_arg_persisted \
        && printf 'warning legacy fallback %s is persisted\n' "$KERNEL_ARG" \
        || printf 'ok      legacy global PCIe workaround is not persisted\n'
    kernel_arg_running \
        && printf 'warning legacy fallback %s is active in this boot\n' "$KERNEL_ARG" \
        || printf 'ok      legacy global PCIe workaround is not active\n'
    if grep -Eq '^[[:space:]]*AutomaticLogin(Enable)?[[:space:]]*=' "$(gdm_config_path)" 2>/dev/null; then
        printf 'warning GDM autologin is still configured\n'
    else
        printf 'ok      GDM autologin disabled\n'
    fi
    local mutter_rc=0
    mutter_ignore_active || mutter_rc=$?
    if [[ "$mutter_rc" == "0" ]]; then
        printf 'ok      Mutter ignores the active NVIDIA endpoint\n'
    elif [[ "$mutter_rc" == "2" ]]; then
        printf 'ok      no active NVIDIA DRM endpoint; Mutter rule is ready\n'
    else
        printf 'relogin Mutter ignore tag is not active on every NVIDIA endpoint\n'
        failures=$((failures + 1))
    fi
    connected="$(cat /sys/devices/platform/asus-nb-wmi/egpu_connected 2>/dev/null || echo unavailable)"
    enabled="$(cat /sys/devices/platform/asus-nb-wmi/egpu_enable 2>/dev/null || echo unavailable)"
    nvidia="$(lspci -Dnnd 10de: 2>/dev/null | tr '\n' ';' || true)"
    printf 'xg: connected=%s enabled=%s\n' "$connected" "$enabled"
    printf 'nvidia-pci: %s\n' "${nvidia:-none}"
    (( failures == 0 )) || exit 10
}

uninstall_mvp() {
    require_root
    [[ $# -le 1 ]] || { usage >&2; exit 64; }
    for command in getent systemctl busctl udevadm; do command_required "$command"; done
    resolve_user "${1:-}"
    local kernel_added=0
    [[ ! -f "$STATE_FILE" ]] || kernel_added="$(state_value kernel_arg_added)"

    systemctl disable --now ghelperd.service 2>/dev/null || true
    rm -f -- "$UNIT_FILE" "$DBUS_FILE" "$POLKIT_FILE" "$UDEV_FILE" \
        "$USER_DEVICE_RULE" \
        "$ROOT_PORT_PM_RULE" \
        "$PORTAL_DROPIN_FILE" "$SUSPEND_HOOK_FILE" "$HID_POWER_FILE" \
        "$NVIDIA_SUSPEND_CONFIG" \
        "$DESKTOP_FILE" "$MARKER_FILE" "$DAEMON_PATH" \
        "$DOC_FILE" \
        "$TARGET_HOME/.config/autostart/$AUTOSTART_NAME" \
        "$TARGET_HOME/.config/autostart/$NVIDIA_AUTOSTART_NAME"
    if [[ -d "$INSTALL_DIR" && ! -L "$INSTALL_DIR" ]]; then
        find -P "$INSTALL_DIR" -depth -delete
    fi
    if [[ "$kernel_added" == "1" ]]; then
        remove_legacy_kernel_arg
    fi
    rmdir "$DAEMON_DIR" "$MARKER_DIR" "$DOC_DIR" 2>/dev/null || true
    rmdir "$PORTAL_DROPIN_DIR" 2>/dev/null || true
    if [[ -d "$STATE_DIR" && ! -L "$STATE_DIR" ]]; then
        find -P "$STATE_DIR" -depth -delete
    fi
    systemctl daemon-reload
    refresh_initramfs
    if systemctl --quiet is-active "user@${TARGET_UID}.service"; then
        user_systemctl daemon-reload
        user_systemctl \
            try-restart xdg-desktop-portal-gnome.service xdg-desktop-portal.service
    fi
    busctl call org.freedesktop.DBus /org/freedesktop/DBus \
        org.freedesktop.DBus ReloadConfig >/dev/null
    udevadm control --reload
    udevadm trigger --subsystem-match=drm --action=add
    echo "G-Helper XG Mobile MVP uninstalled. User configuration was preserved."
    [[ "$kernel_added" != "1" ]] \
        || echo "Reboot once to remove the kernel workaround from the running system."
}

case "${1:-}" in
    install) shift; install_mvp "$@" ;;
    status) shift; status_mvp "$@" ;;
    uninstall) shift; uninstall_mvp "$@" ;;
    -h|--help|help) usage ;;
    *) usage >&2; exit 64 ;;
esac
