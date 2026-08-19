#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_DIR"
STATIC_ONLY=0
if [[ "${1:-}" == "--static-only" && $# == 1 ]]; then
    STATIC_ONLY=1
elif [[ $# != 0 ]]; then
    echo "Usage: scripts/verify-phase2.sh [--static-only]" >&2
    exit 2
fi

fail() {
    echo "ERROR: $*" >&2
    exit 1
}

[[ -f daemon/GHelper.Daemon.csproj && -f daemon/packages.lock.json ]] \
    || fail "locked daemon project is missing"
[[ -f src/Daemon/GHelperDaemonClient.cs ]] || fail "GUI daemon client is missing"
[[ -f packaging/systemd/ghelperd.service \
   && -f packaging/dbus/org.ghelper.Daemon1.conf \
   && -f packaging/polkit/org.ghelper.daemon.policy ]] \
    || fail "package-only privilege assets are incomplete"

rg -q 'InterfaceName = "org\.ghelper\.Daemon1"' daemon/Contract/DaemonContract.cs \
    || fail "D-Bus interface is not major-versioned"
rg -q 'request\.SenderAsString' daemon/Dbus/DaemonMethodHandler.cs \
    || fail "daemon does not use the authenticated D-Bus sender"
rg -q 'GetConnectionUnixUser' daemon/Dbus/DbusCallerIdentityResolver.cs \
    || fail "daemon does not resolve caller UID from D-Bus"
rg -q 'GetConnectionUnixProcessID' daemon/Dbus/DbusCallerIdentityResolver.cs \
    || fail "daemon does not resolve caller PID from D-Bus"
rg -q 'system-bus-name' daemon/Dbus/PolkitAuthorizationService.cs \
    || fail "polkit does not authorize the unique bus sender"
rg -q 'CancelCheckAuthorization' daemon/Dbus/PolkitAuthorizationService.cs \
    || fail "polkit cancellation seam is missing"
rg -q 'cancellationId' daemon/Dbus/PolkitAuthorizationService.cs \
    || fail "polkit does not use per-request cancellation ids"
rg -q 'BoundedCallRunner' daemon/Dbus/*.cs \
    || fail "future D-Bus calls are not bounded"
! rg -q -i 'uid|pid' <(sed -n '/IntrospectionXml =/,/""";/p' daemon/Contract/DaemonContract.cs) \
    || fail "D-Bus contract accepts spoofable caller identity"
! rg -q 'ReplyError\([^)]*ex\.Message|ReplyError\([^;]*\.Message' daemon \
    || fail "daemon exposes an internal exception message"

action_count="$(rg -c '<action id="org\.ghelper\.daemon\.' packaging/polkit/org.ghelper.daemon.policy)"
[[ "$action_count" == "5" ]] || fail "expected five granular polkit actions"
! rg -q 'NOPASSWD|allow_any>yes|allow_inactive>yes|unix-group|wheel' \
    packaging daemon src/Daemon \
    || fail "broad privilege grant found in Phase 2 boundary"
for hardening in \
    'NoNewPrivileges=yes' \
    'CapabilityBoundingSet=CAP_SYS_ADMIN CAP_SYS_MODULE' \
    'AmbientCapabilities=CAP_SYS_ADMIN CAP_SYS_MODULE' \
    'PrivateDevices=no' \
    'ProtectSystem=strict' \
    'ProtectHome=yes' \
    'RestrictAddressFamilies=AF_UNIX AF_NETLINK'; do
    rg -q "^${hardening}$" packaging/systemd/ghelperd.service \
        || fail "daemon unit missing root hardening: $hardening"
done
rg -q '^\[Install\]$' packaging/systemd/ghelperd.service \
    && rg -q '^WantedBy=multi-user\.target$' packaging/systemd/ghelperd.service \
    || fail "MVP daemon unit is not explicitly installable"
! find packaging -type f -name '*.service' ! -path '*/systemd/ghelperd.service' -print -quit | grep -q . \
    || fail "D-Bus activation descriptor found"
! rg -q 'packaging/(systemd|dbus|polkit)|ghelperd\.service|org\.ghelper\.Daemon1\.conf' \
    install src/Program.cs src/App.axaml.cs \
    || fail "legacy installer or GUI startup path bypasses the MVP installer"
rg -q 'GHelperDaemonClient\.ConnectSystemAsync' src/UI/Views/MainWindow.axaml.cs \
    && rg -q 'RequestMutationAsync\(operation\)' src/UI/Views/MainWindow.axaml.cs \
    || fail "XG Mobile UI is not routed through the daemon client"
rg -q 'new XgMobileMutationExecutor\(\)' daemon/Program.cs \
    && rg -q 'mutationExecutionEnabled: true' daemon/Program.cs \
    || fail "production daemon does not enable the reviewed XG executor"
! rg -q '(/sys/|/dev/|File\.(Write|Append|Create)|File\.Open\([^\n]*(Write|ReadWrite)|new FileStream|DllImport|LibraryImport|NativeLibrary|ProcessStartInfo|Process\.Start|/bin/(sh|bash)|\b(sudo|pkexec|systemctl)\b|RunSudoOrPkexec|ServiceController|UnixFileMode)' \
    daemon -g '!**/Hardware/XgMobileMutationExecutor.cs' -g '!**/Hardware/XgMobileHid.cs' \
    || fail "a privileged write path escaped the reviewed XG executor"
rg -q 'GV301QH' daemon/Hardware/XgMobileMutationExecutor.cs \
    && rg -q 'enable-xg-mode|disable-xg-mode' daemon/Hardware/XgMobileMutationExecutor.cs \
    || fail "XG executor model/operation allowlist is missing"

rg -q 'TryRequestNameAsync' daemon/Program.cs \
    || fail "daemon does not handle name acquisition failure"
rg -q 'RequestNameOptions\.None' daemon/Hosting/DaemonStartupContract.cs \
    || fail "daemon ownership can replace or queue behind another owner"
! rg -q 'RequestNameOptions\.Default|AllowReplacement|ReplaceExisting' daemon \
    || fail "daemon service name ownership is replaceable"

for action in \
    set-platform-profile set-charge-limit set-fan-curve set-gpu-mode set-xg-mode; do
    rg -q "org\.ghelper\.daemon\.$action" daemon/Contract/DaemonContract.cs \
        || fail "contract omits polkit action $action"
    rg -q "org\.ghelper\.daemon\.$action" packaging/polkit/org.ghelper.daemon.policy \
        || fail "policy omits action $action"
done

echo "== XG Mobile daemon boundary checks passed =="
if [[ "$STATIC_ONLY" == "1" ]]; then
    exit 0
fi
exec "$SCRIPT_DIR/verify-phase1.sh"
