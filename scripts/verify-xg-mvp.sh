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

bash -n build.sh scripts/build-container.sh scripts/ghelper-xg-mvp.sh
git diff --check

[[ -x scripts/ghelper-xg-mvp.sh ]] || fail "MVP installer is not executable"
[[ -f packaging/autostart/ghelper-xg-mvp.desktop ]] || fail "MVP autostart asset missing"
[[ ! -e packaging/autostart/ghelper-poc-live-xg.desktop ]] || fail "legacy POC autostart asset remains"
[[ ! -e scripts/install-xg-live-poc.sh && ! -e scripts/configure-xg-poc-login.sh ]] \
    || fail "legacy POC installer remains"

rg -q 'InstalledMvpMarkerPath = "/etc/ghelper/xg-mobile-mvp\.conf"' \
    src/Helpers/RuntimeMode.cs || fail "installed runtime marker is missing"
rg -q 'StartupIntent\.InstalledMvp' src/Helpers/RuntimeMode.cs \
    || fail "normal installed runtime mode is missing"
rg -q 'pcie_port_pm=off' scripts/ghelper-xg-mvp.sh README.md \
    || fail "kernel workaround is not managed/documented"
rg -q 'mutter-device-ignore' packaging/udev/61-mutter-ignore-x13-nvidia.rules \
    || fail "Mutter release rule is missing"
rg -q 'system/ghelperd' build.sh scripts/ghelper-xg-mvp.sh \
    || fail "GUI and daemon are not a single build/install artifact"
rg -q '^Exec=/opt/ghelper-xg-mvp/ghelper$' \
    packaging/autostart/ghelper-xg-mvp.desktop \
    || fail "autostart does not use the canonical installed GUI"
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
