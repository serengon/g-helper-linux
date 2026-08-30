#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ARTIFACT_DIR=""
REJECT_DIR=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --also-reject)
            [[ $# -ge 2 ]] || exit 2
            REJECT_DIR="$2"
            shift 2
            ;;
        --help|-h)
            echo "Usage: scripts/verify-artifact.sh [--also-reject /forged/dir] /artifact/dir"
            exit 0
            ;;
        *)
            [[ -z "$ARTIFACT_DIR" ]] || { echo "ERROR: too many artifact paths" >&2; exit 2; }
            ARTIFACT_DIR="$1"
            shift
            ;;
    esac
done
[[ "$ARTIFACT_DIR" == /* ]] || { echo "ERROR: artifact path must be absolute" >&2; exit 2; }
if [[ -n "$REJECT_DIR" && "$REJECT_DIR" != /* ]]; then
    echo "ERROR: rejection artifact path must be absolute" >&2
    exit 2
fi

# shellcheck source=artifact-manifest.sh
source "$SCRIPT_DIR/artifact-manifest.sh"
# shellcheck source=cache-manifest.sh
source "$SCRIPT_DIR/cache-manifest.sh"
# shellcheck source=provenance.sh
source "$SCRIPT_DIR/provenance.sh"

IMAGE_INPUT_HASH="$({
    for input in Containerfile.build .dockerignore global.json Directory.Build.props; do
        printf '%s\0' "$input"
        sha256sum "$REPO_DIR/$input" | cut -d' ' -f1
    done
} | sha256sum | cut -d' ' -f1)"
CACHE_INPUT_HASH="$(ghelper_cache_input_sha256 "$REPO_DIR")"
IMAGE_ID="$(GHELPER_OFFLINE=1 "$SCRIPT_DIR/build-container.sh" --print-image-id)"

user_cache_root="${XDG_CACHE_HOME:-}"
if [[ "$user_cache_root" != /* ]]; then
    [[ "${HOME:-}" == /* ]] || { echo "ERROR: absolute cache root unavailable" >&2; exit 1; }
    user_cache_root="$HOME/.cache"
fi
SHARED_NUGET="$user_cache_root/ghelper-x13-build/$CACHE_INPUT_HASH/nuget"
ghelper_nuget_validate_tree "$SHARED_NUGET"

verify_root="$(mktemp -d -t ghelper-artifact-verify.XXXXXX)"
chmod 700 "$verify_root"
cleanup_verify_root() {
    if [[ "$verify_root" == /tmp/ghelper-artifact-verify.* \
       && -d "$verify_root" && ! -L "$verify_root" ]]; then
        find -P "$verify_root" -depth -delete
    fi
}
trap cleanup_verify_root EXIT

private_nuget="$verify_root/nuget"
ghelper_nuget_materialize_closure "$REPO_DIR" "$SHARED_NUGET" "$private_nuget"
NUGET_MANIFEST_HASH="$(ghelper_nuget_manifest_sha256 "$private_nuget")"
ENVIRONMENT_HASH="$({
    printf 'format=ghelper-phase1-environment-v2\n'
    printf 'image_id=%s\n' "$IMAGE_ID"
    printf 'image_input_sha256=%s\n' "$IMAGE_INPUT_HASH"
    printf 'cache_input_sha256=%s\n' "$CACHE_INPUT_HASH"
    printf 'nuget_manifest_sha256=%s\n' "$NUGET_MANIFEST_HASH"
} | sha256sum | cut -d' ' -f1)"
if [[ -n "$(git -C "$REPO_DIR" status --porcelain=v1 --untracked-files=all)" ]]; then
    [[ "${GHELPER_DIRTY_REVIEW:-0}" == "1" ]] || {
        echo "ERROR: dirty source verification requires GHELPER_DIRTY_REVIEW=1" >&2
        exit 1
    }
    GHELPER_DIRTY_REVIEW=1 ghelper_set_provenance "$REPO_DIR" "$ENVIRONMENT_HASH"
else
    GHELPER_DIRTY_REVIEW=0 ghelper_set_provenance "$REPO_DIR" "$ENVIRONMENT_HASH"
fi

verify_static_unsigned_manifest() {
    local candidate="$1" expected actual
    expected="$verify_root/expected.$(printf '%s' "$candidate" | sha256sum | cut -c1-16)"
    actual="$candidate/$GHELPER_ARTIFACT_MANIFEST_NAME"
    ghelper_artifact_validate_tree "$candidate"
    [[ -f "$actual" && ! -L "$actual" && "$(stat -c %a "$actual")" == "600" ]] || {
        echo "ERROR: unsigned external build manifest missing or unsafe: $candidate" >&2
        return 1
    }
    ghelper_render_artifact_manifest \
        "$candidate" "$GHELPER_INFORMATIONAL_VERSION" "$GHELPER_BUILD_PROVENANCE" \
        "$GHELPER_BUILD_MODE" "$IMAGE_ID" "$IMAGE_INPUT_HASH" "$CACHE_INPUT_HASH" \
        "$NUGET_MANIFEST_HASH" "$ENVIRONMENT_HASH" > "$expected"
    cmp -s "$expected" "$actual" || {
        echo "ERROR: unsigned external manifest does not match local reproducible inputs/tree" >&2
        return 1
    }
}

# Static consistency is explicitly not authentication: an unsigned caller can
# fabricate both a malicious executable and matching hashes/text. No candidate
# executable is launched at this stage (or anywhere before byte identity).
verify_static_unsigned_manifest "$ARTIFACT_DIR"
if [[ -n "$REJECT_DIR" ]]; then
    verify_static_unsigned_manifest "$REJECT_DIR"
fi

# Trust decision: independently rebuild the current source/environment and
# require byte-identical payload and external manifest. A signature will replace
# this expensive local-rebuild trust path only in the future RPM phase.
rebuild_output="$verify_root/rebuilt"
if [[ "$GHELPER_BUILD_MODE" == "dirty-review" ]]; then
    GHELPER_DIRTY_REVIEW=1 GHELPER_OFFLINE=1 \
        "$SCRIPT_DIR/build-container.sh" --output "$rebuild_output" > "$verify_root/rebuild.log" 2>&1
else
    GHELPER_OFFLINE=1 "$SCRIPT_DIR/build-container.sh" \
        --output "$rebuild_output" > "$verify_root/rebuild.log" 2>&1
fi
verify_static_unsigned_manifest "$rebuild_output"
cmp -s "$ARTIFACT_DIR/ghelper" "$rebuild_output/ghelper" || {
    echo "ERROR: candidate executable differs from independent rebuild" >&2
    exit 1
}
cmp -s "$ARTIFACT_DIR/$GHELPER_ARTIFACT_MANIFEST_NAME" \
       "$rebuild_output/$GHELPER_ARTIFACT_MANIFEST_NAME" || {
    echo "ERROR: artifact is internally coherent but differs from independent rebuild" >&2
    exit 1
}

# Execute only the independently rebuilt, now byte-identified binary. The
# candidate path is never executed, even after identity, and REJECT_DIR is
# always treated as untrusted data.
cli_output="$(HOME="$verify_root/home" XDG_CONFIG_HOME="$verify_root/config" \
    XDG_CACHE_HOME="$verify_root/runtime-cache" XDG_DATA_HOME="$verify_root/data" \
    "$rebuild_output/ghelper" --print-build-metadata)"
[[ "$cli_output" == $'LOCAL-REPRODUCIBLE-UNSIGNED\t'"$GHELPER_INFORMATIONAL_VERSION" ]] || {
    echo "ERROR: independently rebuilt binary metadata mismatch" >&2
    exit 1
}

if [[ -n "$REJECT_DIR" ]]; then
    if cmp -s "$REJECT_DIR/ghelper" "$rebuild_output/ghelper" || \
       cmp -s "$REJECT_DIR/$GHELPER_ARTIFACT_MANIFEST_NAME" \
              "$rebuild_output/$GHELPER_ARTIFACT_MANIFEST_NAME"; then
        echo "ERROR: expected forged artifact is byte-identical to canonical rebuild" >&2
        exit 1
    fi
    echo "Rejected internally coherent unsigned forgery by independent rebuild."
fi
echo "Verified by independent local reproducible rebuild (unsigned Phase 1 evidence)."
