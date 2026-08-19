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
DESKTOP_FILE="/usr/share/applications/ghelper-xg-mvp.desktop"
DOC_DIR="/usr/share/doc/ghelper-xg-mvp"
DOC_FILE="$DOC_DIR/privilege-boundary.md"
AUTOSTART_NAME="ghelper-xg-mvp.desktop"
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
Installation adds the Mutter udev rule and, when absent, $KERNEL_ARG.
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
}

kernel_arg_running() {
    [[ -r /proc/cmdline ]] || return 1
    grep -Eq "(^|[[:space:]])${KERNEL_ARG}([[:space:]]|$)" /proc/cmdline
}

kernel_arg_in_all_entries() {
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

kernel_arg_persisted() {
    if [[ -r /etc/kernel/cmdline ]]; then
        grep -Eq "(^|[[:space:]])${KERNEL_ARG}([[:space:]]|$)" /etc/kernel/cmdline
        return
    fi
    kernel_arg_in_all_entries
}

ensure_kernel_cmdline_token() {
    local file=/etc/kernel/cmdline content stage
    [[ -f "$file" && ! -L "$file" ]] \
        || die "Fedora kernel command line is missing or unsafe: $file"
    content="$(cat "$file")"
    [[ "$content" != *$'\n'* ]] || die "$file must contain exactly one command line"
    case " $content " in
        *" $KERNEL_ARG "*) return 0 ;;
    esac
    stage="$(mktemp /etc/kernel/.ghelper-cmdline.XXXXXX)"
    printf '%s %s\n' "$content" "$KERNEL_ARG" >"$stage"
    chown root:root "$stage"
    chmod 0644 "$stage"
    mv -f -- "$stage" "$file"
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
    local gdm=/etc/gdm/custom.conf backup=/etc/gdm/custom.conf.ghelper-poc-backup
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
    for command in getent realpath grubby systemctl busctl udevadm sha256sum; do
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
        "$REPO_DIR/packaging/autostart/$AUTOSTART_NAME" \
        "$REPO_DIR/docs/privilege-boundary.md"; do
        validate_asset "$asset"
    done

    install -d -o root -g root -m 0700 "$STATE_DIR"
    if [[ -f "$STATE_FILE" ]]; then
        [[ "$(state_value user)" == "$TARGET_USER" ]] \
            || die "existing MVP belongs to a different user"
        KERNEL_ARG_ADDED="$(state_value kernel_arg_added)"
    elif kernel_arg_in_all_entries; then
        KERNEL_ARG_ADDED=0
    else
        KERNEL_ARG_ADDED=1
    fi

    install_gui_tree
    install -d -o root -g root -m 0755 "$DAEMON_DIR" "$MARKER_DIR"
    install -o root -g root -m 0755 "$DIST_DIR_REAL/system/ghelperd" "$DAEMON_PATH"
    install -o root -g root -m 0644 "$REPO_DIR/packaging/systemd/ghelperd.service" "$UNIT_FILE"
    install -o root -g root -m 0644 "$REPO_DIR/packaging/dbus/org.ghelper.Daemon1.conf" "$DBUS_FILE"
    install -o root -g root -m 0644 "$REPO_DIR/packaging/polkit/org.ghelper.daemon.policy" "$POLKIT_FILE"
    install -o root -g root -m 0644 "$REPO_DIR/packaging/udev/61-mutter-ignore-x13-nvidia.rules" "$UDEV_FILE"
    install -o root -g root -m 0644 "$REPO_DIR/packaging/autostart/$AUTOSTART_NAME" "$DESKTOP_FILE"
    install -d -o root -g root -m 0755 "$DOC_DIR"
    install -o root -g root -m 0644 "$REPO_DIR/docs/privilege-boundary.md" "$DOC_FILE"
    {
        printf 'mode=%s\n' "$MVP_MODE"
        printf 'model=%s\n' "$MODEL_TOKEN"
    } >"$MARKER_FILE"
    chown root:root "$MARKER_FILE"
    chmod 0644 "$MARKER_FILE"

    if ! kernel_arg_in_all_entries; then
        grubby --update-kernel=ALL --args="$KERNEL_ARG"
    fi
    ensure_kernel_cmdline_token
    kernel_arg_in_all_entries || die "failed to persist $KERNEL_ARG"
    kernel_arg_persisted || die "failed to persist $KERNEL_ARG for future kernels"

    install_user_files
    restore_poc_autologin
    write_state

    systemctl daemon-reload
    busctl call org.freedesktop.DBus /org/freedesktop/DBus \
        org.freedesktop.DBus ReloadConfig >/dev/null
    udevadm control --reload
    udevadm trigger --subsystem-match=drm --action=add
    systemctl enable --now ghelperd.service
    systemctl restart ghelperd.service
    systemctl --quiet is-active ghelperd.service \
        || die "ghelperd did not become active"

    echo
    echo "G-Helper XG Mobile MVP installed for $TARGET_USER."
    if kernel_arg_running; then
        echo "Kernel workaround already active. Log out/in once if Mutter has not seen the udev rule."
    else
        echo "REBOOT REQUIRED: the current kernel does not yet have $KERNEL_ARG."
    fi
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
    [[ "$found" == "1" ]]
}

status_mvp() {
    [[ $# -le 1 ]] || { usage >&2; exit 64; }
    for command in getent systemctl grubby udevadm; do command_required "$command"; done
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
    check_file 'D-Bus policy' "$DBUS_FILE" || failures=$((failures + 1))
    check_file 'polkit policy' "$POLKIT_FILE" || failures=$((failures + 1))
    check_file 'installed MVP documentation' "$DOC_FILE" || failures=$((failures + 1))
    check_file 'user autostart' "$TARGET_HOME/.config/autostart/$AUTOSTART_NAME" \
        || failures=$((failures + 1))
    if systemctl --quiet is-active ghelperd.service; then
        printf 'ok      ghelperd active\n'
    else
        printf 'missing ghelperd active\n'
        failures=$((failures + 1))
    fi
    if kernel_arg_persisted; then
        printf 'ok      %s persisted\n' "$KERNEL_ARG"
    else
        printf 'missing %s in persistent kernel command line\n' "$KERNEL_ARG"
        failures=$((failures + 1))
    fi
    if kernel_arg_running; then
        printf 'ok      %s active\n' "$KERNEL_ARG"
    else
        printf 'reboot  %s not active in the running kernel\n' "$KERNEL_ARG"
        failures=$((failures + 1))
    fi
    if grep -Eq '^[[:space:]]*AutomaticLogin(Enable)?[[:space:]]*=' /etc/gdm/custom.conf 2>/dev/null; then
        printf 'warning GDM autologin is still configured\n'
    else
        printf 'ok      GDM autologin disabled\n'
    fi
    if mutter_ignore_active; then
        printf 'ok      Mutter ignores the active NVIDIA endpoint\n'
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
    for command in getent grubby systemctl busctl udevadm; do command_required "$command"; done
    resolve_user "${1:-}"
    local kernel_added=0
    [[ ! -f "$STATE_FILE" ]] || kernel_added="$(state_value kernel_arg_added)"

    systemctl disable --now ghelperd.service 2>/dev/null || true
    rm -f -- "$UNIT_FILE" "$DBUS_FILE" "$POLKIT_FILE" "$UDEV_FILE" \
        "$DESKTOP_FILE" "$MARKER_FILE" "$DAEMON_PATH" \
        "$DOC_FILE" \
        "$TARGET_HOME/.config/autostart/$AUTOSTART_NAME"
    if [[ -d "$INSTALL_DIR" && ! -L "$INSTALL_DIR" ]]; then
        find -P "$INSTALL_DIR" -depth -delete
    fi
    if [[ "$kernel_added" == "1" ]]; then
        grubby --update-kernel=ALL --remove-args="$KERNEL_ARG"
        remove_kernel_cmdline_token
    fi
    rmdir "$DAEMON_DIR" "$MARKER_DIR" "$DOC_DIR" 2>/dev/null || true
    if [[ -d "$STATE_DIR" && ! -L "$STATE_DIR" ]]; then
        find -P "$STATE_DIR" -depth -delete
    fi
    systemctl daemon-reload
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
