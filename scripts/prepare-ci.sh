#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
runner_temp="${RUNNER_TEMP:-}"

[[ "$runner_temp" == /* && -d "$runner_temp" && ! -L "$runner_temp" \
   && "$(realpath -e -- "$runner_temp")" == "$runner_temp" \
   && "$(stat -c %u -- "$runner_temp")" == "$(id -u)" ]] || {
    echo "ERROR: RUNNER_TEMP must be an absolute user-owned real directory" >&2
    exit 2
}

ci_root="$(mktemp -d "$runner_temp/ghelper-phase1-prepare.XXXXXX")"
chmod 700 "$ci_root"
cleanup_ci_root() {
    if [[ "$ci_root" == "$runner_temp/ghelper-phase1-prepare."* \
       && -d "$ci_root" && ! -L "$ci_root" \
       && "$(stat -c %u -- "$ci_root")" == "$(id -u)" \
       && "$(stat -c %a -- "$ci_root")" == "700" ]]; then
        find -P "$ci_root" -depth -delete
    fi
}
trap cleanup_ci_root EXIT

ci_cache="$runner_temp/ghelper-phase1-cache"
if [[ ! -e "$ci_cache" && ! -L "$ci_cache" ]]; then
    mkdir -m 700 "$ci_cache"
fi
[[ -d "$ci_cache" && ! -L "$ci_cache" \
   && "$(realpath -e -- "$ci_cache")" == "$ci_cache" \
   && "$(stat -c %u -- "$ci_cache")" == "$(id -u)" \
   && "$(stat -c %a -- "$ci_cache")" == "700" ]] || {
    echo "ERROR: CI cache must be a private RUNNER_TEMP child" >&2
    exit 2
}

XDG_CACHE_HOME="$ci_cache" "$REPO_DIR/build.sh" --no-aot --output "$ci_root/output"
