#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ORIGINAL_ARGS=("$@")
ENGINE="${CONTAINER_ENGINE:-docker}"
OFFLINE="${GHELPER_OFFLINE:-0}"
caller_provenance="${GHELPER_BUILD_PROVENANCE:-}"
caller_version="${GHELPER_INFORMATIONAL_VERSION:-}"
unset GHELPER_BUILD_PROVENANCE GHELPER_INFORMATIONAL_VERSION \
    GHELPER_BUILD_ENV_HASH GHELPER_BUILD_MODE GHELPER_DIFF_HASH
if [[ -n "$caller_provenance$caller_version" ]]; then
    echo "Ignoring caller-supplied provenance metadata; recomputed from staged inputs." >&2
fi
OUTPUT_DIR="$REPO_DIR/dist"
OUTPUT_MARKER_NAME=".ghelper-x13-output-owner-v1"
PRINT_IMAGE=0
PRINT_PROVENANCE=0
BUILD_ARGS=()
CACHE_LOCK_HELD=0
CACHE_LOCK_FD=""

if [[ "${1:-}" == "--internal-cache-lock" ]]; then
    [[ $# -ge 2 && "${GHELPER_CACHE_LOCK_FD:-}" == "$2" ]] || {
        echo "ERROR: invalid internal cache-lock invocation" >&2
        exit 1
    }
    CACHE_LOCK_HELD=1
    CACHE_LOCK_FD="$2"
    shift 2
    ORIGINAL_ARGS=("$@")
fi

while [[ $# -gt 0 ]]; do
    case "$1" in
        --output)
            [[ $# -ge 2 && "$2" == /* ]] || {
                echo "ERROR: --output requires an absolute path" >&2
                exit 2
            }
            OUTPUT_DIR="$2"
            shift 2
            ;;
        --print-image-id)
            PRINT_IMAGE=1
            shift
            ;;
        --print-provenance)
            PRINT_PROVENANCE=1
            shift
            ;;
        --no-aot|--fast|-f)
            BUILD_ARGS+=("$1")
            shift
            ;;
        -h|--help)
            cat <<EOF
Usage: ./build.sh [--no-aot] [--output /absolute/path]
       ./build.sh --print-image-id
       ./build.sh --print-provenance

Every compilation is staged in a private source snapshot and uses a private,
read-only, fully manifested NuGet cache. The default output is dist/.
EOF
            exit 0
            ;;
        *)
            echo "ERROR: unknown build option: $1" >&2
            exit 2
            ;;
    esac
done

user_cache_root="${XDG_CACHE_HOME:-}"
if [[ "$user_cache_root" != /* ]]; then
    [[ "${HOME:-}" == /* ]] || {
        echo "ERROR: an absolute HOME or XDG_CACHE_HOME is required for the build cache" >&2
        exit 1
    }
    user_cache_root="$HOME/.cache"
fi
# Validate before invoking Docker or Podman: container engines may themselves
# write below XDG_CACHE_HOME and must never see an attacker-controlled symlink.
python3 "$SCRIPT_DIR/cache-lock-exec.py" validate-root "$user_cache_root"

validate_output_path() {
    local normalized parent parent_mode parent_owner
    [[ "$OUTPUT_DIR" == /* && "$OUTPUT_DIR" != *$'\n'* ]] || return 1
    normalized="$(realpath -m -- "$OUTPUT_DIR")" || return 1
    [[ "$normalized" == "$OUTPUT_DIR" ]] || return 1
    case "$OUTPUT_DIR" in
        /|/tmp|"${HOME:-/nonexistent}"|"$REPO_DIR") return 1 ;;
    esac
    case "$REPO_DIR/" in "$OUTPUT_DIR/"*) return 1 ;; esac
    if [[ "${HOME:-}" == /* ]]; then
        case "$HOME/" in "$OUTPUT_DIR/"*) return 1 ;; esac
    fi
    parent="$(dirname -- "$OUTPUT_DIR")"
    [[ -d "$parent" && ! -L "$parent" ]] || return 1
    [[ "$(realpath -e -- "$parent")" == "$parent" ]] || return 1
    parent_owner="$(stat -c %u -- "$parent")"
    parent_mode="$(stat -c %a -- "$parent")"
    [[ "$parent_owner" == "$(id -u)" ]] || return 1
    (( (8#$parent_mode & 0022) == 0 )) || return 1
}

if [[ "$PRINT_IMAGE" == "0" && "$PRINT_PROVENANCE" == "0" ]]; then
    validate_output_path || {
        echo "ERROR: refusing unsafe build output path: $OUTPUT_DIR" >&2
        exit 2
    }

    output_key="$(printf '%s' "$OUTPUT_DIR" | sha256sum | cut -d' ' -f1)"
    output_registry_root="$user_cache_root/ghelper-x13-build/output-owners"
    output_registry="$output_registry_root/$output_key"
    output_was_existing=0

    owned_marker_text() {
        printf 'format=ghelper-x13-output-owner-v1\npath_sha256=%s\nnonce=%s\n' \
            "$output_key" "$output_nonce"
    }

    validate_owned_output() {
        local candidate="$1" expected_marker mode owner
        [[ -d "$candidate" && ! -L "$candidate" ]] || return 1
        [[ "$(stat -c %u -- "$candidate")" == "$(id -u)" \
           && "$(stat -c %a -- "$candidate")" == "700" ]] || return 1
        [[ -f "$candidate/$OUTPUT_MARKER_NAME" \
           && ! -L "$candidate/$OUTPUT_MARKER_NAME" ]] || return 1
        [[ -f "$output_registry" && ! -L "$output_registry" ]] || return 1
        for expected_marker in "$candidate/$OUTPUT_MARKER_NAME" "$output_registry"; do
            owner="$(stat -c %u -- "$expected_marker")"
            mode="$(stat -c %a -- "$expected_marker")"
            [[ "$owner" == "$(id -u)" && "$mode" == "600" ]] || return 1
            [[ "$(cat -- "$expected_marker")" == "$(owned_marker_text)" ]] || return 1
        done
    }

    safe_delete_owned_output() {
        local candidate="$1"
        validate_owned_output "$candidate" || {
            echo "ERROR: refusing to delete unowned build output: $candidate" >&2
            return 1
        }
        find -P "$candidate" -depth -delete
    }

    if [[ -e "$OUTPUT_DIR" || -L "$OUTPUT_DIR" ]]; then
        [[ -d "$OUTPUT_DIR" && ! -L "$OUTPUT_DIR" \
           && -f "$OUTPUT_DIR/$OUTPUT_MARKER_NAME" ]] || {
            echo "ERROR: existing output is not a builder-owned directory: $OUTPUT_DIR" >&2
            exit 2
        }
        output_nonce="$(sed -n 's/^nonce=//p' "$OUTPUT_DIR/$OUTPUT_MARKER_NAME")"
        [[ "$output_nonce" =~ ^[0-9a-f]{64}$ ]] || {
            echo "ERROR: existing output has an invalid ownership marker" >&2
            exit 2
        }
        validate_owned_output "$OUTPUT_DIR" || {
            echo "ERROR: existing output ownership validation failed: $OUTPUT_DIR" >&2
            exit 2
        }
        output_was_existing=1
    fi
fi

[[ "$PRINT_IMAGE" == "0" || ( "$PRINT_PROVENANCE" == "0" && "${#BUILD_ARGS[@]}" == "0" ) ]] || {
    echo "ERROR: --print-image-id cannot be combined with build options" >&2
    exit 2
}
[[ "$PRINT_PROVENANCE" == "0" || "${#BUILD_ARGS[@]}" == "0" ]] || {
    echo "ERROR: --print-provenance cannot be combined with build options" >&2
    exit 2
}
if [[ -n "${GHELPER_BUILD_IMAGE:-}" ]]; then
    echo "ERROR: GHELPER_BUILD_IMAGE overrides are forbidden; the canonical image is mandatory." >&2
    exit 1
fi
command -v "$ENGINE" >/dev/null 2>&1 || {
    echo "ERROR: container engine not found: $ENGINE" >&2
    exit 1
}

container_user_args=(--user "$(id -u):$(id -g)")
if [[ "$(basename -- "$ENGINE")" == "podman" ]]; then
    # Rootless Podman otherwise interprets --user inside its subordinate-ID
    # namespace, so bind-mounted cache paths owned by the caller become
    # unwritable. keep-id maps the caller UID/GID identically in the container.
    container_user_args=(--userns=keep-id --user "$(id -u):$(id -g)")
fi

IMAGE_INPUT_HASH="$({
    for input in Containerfile.build .dockerignore global.json Directory.Build.props; do
        printf '%s\0' "$input"
        sha256sum "$REPO_DIR/$input" | cut -d' ' -f1
    done
} | sha256sum | cut -d' ' -f1)"
IMAGE="ghelper-x13-build:${IMAGE_INPUT_HASH:0:16}"

image_exists=0
"$ENGINE" image inspect "$IMAGE" >/dev/null 2>&1 && image_exists=1
if [[ "$OFFLINE" == "1" && "$image_exists" == "0" ]]; then
    echo "ERROR: offline canonical build image is missing: $IMAGE" >&2
    exit 1
fi
if [[ "$image_exists" == "0" ]]; then
    echo "Building canonical pinned SDK image: $IMAGE"
    "$ENGINE" build --pull=false \
        --label "org.ghelper.x13.build-input=$IMAGE_INPUT_HASH" \
        -f "$REPO_DIR/Containerfile.build" -t "$IMAGE" "$REPO_DIR"
fi
image_label="$("$ENGINE" image inspect --format \
    '{{index .Config.Labels "org.ghelper.x13.build-input"}}' "$IMAGE")"
[[ "$image_label" == "$IMAGE_INPUT_HASH" ]] || {
    echo "ERROR: canonical build image label mismatch" >&2
    exit 1
}
IMAGE_ID="$("$ENGINE" image inspect --format '{{.Id}}' "$IMAGE")"
# Docker prefixes local image IDs with "sha256:" while Podman 5 returns the
# same 64 hexadecimal characters without the algorithm label. Normalize both
# to the canonical form used by the build context and artifact manifest.
if [[ "$IMAGE_ID" =~ ^[0-9a-f]{64}$ ]]; then
    IMAGE_ID="sha256:$IMAGE_ID"
fi
[[ "$IMAGE_ID" =~ ^sha256:[0-9a-f]{64}$ ]] || {
    echo "ERROR: invalid immutable image ID: $IMAGE_ID" >&2
    exit 1
}
if [[ "$PRINT_IMAGE" == "1" ]]; then
    printf '%s\n' "$IMAGE_ID"
    exit 0
fi

# Cache identity is separate from image identity and covers the complete
# project/props/global.json/four-lock-file closure.
# shellcheck source=cache-manifest.sh
source "$REPO_DIR/scripts/cache-manifest.sh"
CACHE_INPUT_HASH="$(ghelper_cache_input_sha256 "$REPO_DIR")"
[[ "$CACHE_INPUT_HASH" =~ ^[0-9a-f]{64}$ ]] || {
    echo "ERROR: invalid locked cache-input identity" >&2
    exit 1
}
CACHE_ROOT="$user_cache_root/ghelper-x13-build/$CACHE_INPUT_HASH"
SHARED_NUGET="$CACHE_ROOT/nuget"
SHARED_DOTNET="$CACHE_ROOT/dotnet"
if [[ "$CACHE_LOCK_HELD" == "0" ]]; then
    exec python3 "$SCRIPT_DIR/cache-lock-exec.py" lock-and-exec \
        "$user_cache_root" "$CACHE_INPUT_HASH" "$SCRIPT_DIR/build-container.sh" \
        "${ORIGINAL_ARGS[@]}"
fi
python3 "$SCRIPT_DIR/cache-lock-exec.py" validate-held \
    "$user_cache_root" "$CACHE_INPUT_HASH" "$CACHE_LOCK_FD"

snapshot_parent="$(mktemp -d -t ghelper-build-snapshot.XXXXXX)"
snapshot_dir="$snapshot_parent/source"
snapshot_patch="$snapshot_parent/worktree.patch"
private_nuget="$snapshot_parent/nuget"
nuget_manifest="$snapshot_parent/nuget-cache.manifest"
context_file="$snapshot_parent/build-context"
token_file="$snapshot_parent/build-token"
output_stage=""
cleanup_snapshot() {
    if [[ -n "$output_stage" && -e "$output_stage" ]]; then
        safe_delete_owned_output "$output_stage" || true
    fi
    if [[ "$snapshot_parent" == /tmp/ghelper-build-snapshot.* && -d "$snapshot_parent" ]]; then
        rm -rf -- "$snapshot_parent"
    fi
}
trap cleanup_snapshot EXIT

head_commit="$(git -C "$REPO_DIR" rev-parse HEAD)"
git -c init.defaultBranch=x13-hardened clone --quiet --no-hardlinks --no-tags \
    "$REPO_DIR" "$snapshot_dir"
git -c advice.detachedHead=false -C "$snapshot_dir" \
    checkout --quiet --detach "$head_commit"
git -C "$REPO_DIR" diff --binary --full-index --no-ext-diff HEAD -- > "$snapshot_patch"
if [[ -s "$snapshot_patch" ]]; then
    git -C "$snapshot_dir" apply --binary "$snapshot_patch"
fi
while IFS= read -r -d '' path; do
    mkdir -p "$snapshot_dir/$(dirname "$path")"
    cp -a -- "$REPO_DIR/$path" "$snapshot_dir/$path"
done < <(git -C "$REPO_DIR" ls-files --others --exclude-standard -z | LC_ALL=C sort -z)

network_args=()
if [[ "$OFFLINE" == "1" ]]; then
    network_args=(--network none)
else
    echo "Preparing shared locked NuGet cache (artifact build still uses a private snapshot)..."
    "$ENGINE" run --rm \
        "${container_user_args[@]}" \
        --env HOME=/tmp \
        --env DOTNET_CLI_HOME=/dotnet \
        --env NUGET_PACKAGES=/nuget \
        --volume "$snapshot_dir:/work" \
        --volume "$SHARED_DOTNET:/dotnet" \
        --volume "$SHARED_NUGET:/nuget" \
        --workdir /work \
        "$IMAGE_ID" bash -ceu '
            dotnet restore src/GHelper.Linux.csproj --runtime linux-x64 --locked-mode
            dotnet restore daemon/GHelper.Daemon.csproj --runtime linux-x64 --locked-mode
            dotnet restore tests/GHelper.Linux.Tests/GHelper.Linux.Tests.csproj --locked-mode
            dotnet restore audio-helper/tests/cs/TestAudioPipeline.csproj --locked-mode
        '
fi
ghelper_nuget_validate_tree "$SHARED_NUGET" || exit 1
python3 "$SCRIPT_DIR/cache-lock-exec.py" validate-held \
    "$user_cache_root" "$CACHE_INPUT_HASH" "$CACHE_LOCK_FD"
find -P "$SHARED_NUGET" -type f -print -quit | grep -q . || {
    echo "ERROR: canonical NuGet cache is empty; prepare it once without GHELPER_OFFLINE=1" >&2
    exit 1
}

ghelper_nuget_materialize_closure "$snapshot_dir" "$SHARED_NUGET" "$private_nuget"
# Use the snapshotted implementation for all subsequent build-input checks.
source "$snapshot_dir/scripts/cache-manifest.sh"
[[ "$(ghelper_cache_input_sha256 "$snapshot_dir")" == "$CACHE_INPUT_HASH" ]] || {
    echo "ERROR: staged cache-input identity changed" >&2
    exit 1
}
ghelper_nuget_manifest "$private_nuget" > "$nuget_manifest"
NUGET_MANIFEST_HASH="$(sha256sum "$nuget_manifest" | cut -d' ' -f1)"
ENVIRONMENT_HASH="$({
    printf 'format=ghelper-phase1-environment-v2\n'
    printf 'image_id=%s\n' "$IMAGE_ID"
    printf 'image_input_sha256=%s\n' "$IMAGE_INPUT_HASH"
    printf 'cache_input_sha256=%s\n' "$CACHE_INPUT_HASH"
    printf 'nuget_manifest_sha256=%s\n' "$NUGET_MANIFEST_HASH"
} | sha256sum | cut -d' ' -f1)"

# Derive source provenance inside the isolated snapshot, never from caller
# metadata. Dirty-review permission is scoped only to that exact snapshot.
source "$snapshot_dir/scripts/provenance.sh"
if [[ -n "$(git -C "$snapshot_dir" status --porcelain=v1 --untracked-files=all)" ]]; then
    [[ "${GHELPER_DIRTY_REVIEW:-0}" == "1" ]] || {
        echo "ERROR: dirty tree refused; release/default builds require a clean fork commit" >&2
        exit 1
    }
    GHELPER_DIRTY_REVIEW=1 ghelper_set_provenance "$snapshot_dir" "$ENVIRONMENT_HASH"
else
    GHELPER_DIRTY_REVIEW=0 ghelper_set_provenance "$snapshot_dir" "$ENVIRONMENT_HASH"
fi
snapshot_source_provenance="$GHELPER_BUILD_PROVENANCE"
snapshot_mode="$GHELPER_BUILD_MODE"
snapshot_version="$GHELPER_INFORMATIONAL_VERSION"
wrapper_nonce="$(od -An -tx1 -N32 /dev/urandom | tr -d ' \n')"
[[ "$wrapper_nonce" =~ ^[0-9a-f]{64}$ ]] || {
    echo "ERROR: failed to create wrapper handshake nonce" >&2
    exit 1
}

{
    printf 'format=ghelper-phase1-build-context-v1\n'
    printf 'wrapper_nonce=%s\n' "$wrapper_nonce"
    printf 'source_provenance=%s\n' "$snapshot_source_provenance"
    printf 'build_mode=%s\n' "$snapshot_mode"
    printf 'informational_version=%s\n' "$snapshot_version"
    printf 'image_id=%s\n' "$IMAGE_ID"
    printf 'image_input_sha256=%s\n' "$IMAGE_INPUT_HASH"
    printf 'cache_input_sha256=%s\n' "$CACHE_INPUT_HASH"
    printf 'nuget_manifest_sha256=%s\n' "$NUGET_MANIFEST_HASH"
    printf 'environment_sha256=%s\n' "$ENVIRONMENT_HASH"
    printf 'nuget_manifest_begin\n'
    cat "$nuget_manifest"
    printf 'nuget_manifest_end\n'
} > "$context_file"
printf 'ghelper-phase1-wrapper-v1:%s\n' "$wrapper_nonce" > "$token_file"
chmod 400 "$context_file" "$token_file"
printf 'Staged build snapshot: %s\n' "$snapshot_version"

if [[ "$PRINT_PROVENANCE" == "1" ]]; then
    printf '%s\n' "$snapshot_version"
    exit 0
fi

set +e
"$ENGINE" run --rm \
    "${network_args[@]}" \
    "${container_user_args[@]}" \
    --env HOME=/tmp \
    --env XDG_CONFIG_HOME=/tmp/ghelper-build-config \
    --env XDG_CACHE_HOME=/tmp/ghelper-build-cache \
    --env XDG_DATA_HOME=/tmp/ghelper-build-data \
    --env DOTNET_CLI_HOME=/tmp/ghelper-dotnet \
    --env NUGET_PACKAGES=/nuget \
    --volume "$snapshot_dir:/work" \
    --volume "$private_nuget:/nuget:ro" \
    --volume "$context_file:/tmp/ghelper-build-context:ro" \
    --volume "$token_file:/tmp/ghelper-build-token:ro" \
    --workdir /work \
    "$IMAGE_ID" ./build.sh "${BUILD_ARGS[@]}"
build_rc=$?
set -e
[[ "$build_rc" == "0" ]] || exit "$build_rc"

post_nuget_hash="$(ghelper_nuget_manifest_sha256 "$private_nuget")"
[[ "$post_nuget_hash" == "$NUGET_MANIFEST_HASH" ]] || {
    echo "ERROR: private NuGet cache changed during compilation" >&2
    exit 1
}
if [[ "$snapshot_mode" == "dirty-review" ]]; then
    GHELPER_DIRTY_REVIEW=1 ghelper_set_provenance "$snapshot_dir" "$ENVIRONMENT_HASH"
else
    GHELPER_DIRTY_REVIEW=0 ghelper_set_provenance "$snapshot_dir" "$ENVIRONMENT_HASH"
fi
[[ "$GHELPER_INFORMATIONAL_VERSION" == "$snapshot_version" ]] || {
    echo "ERROR: staged build inputs changed during compilation" >&2
    exit 1
}
[[ -d "$snapshot_dir/dist" ]] || {
    echo "ERROR: staged build produced no output" >&2
    exit 1
}

# The binary contains only unsigned local provenance. The canonical evidence
# is external, bound to the exact artifact tree, and must be checked by
# scripts/verify-artifact.sh (which rebuilds by default).
# shellcheck source=artifact-manifest.sh
source "$snapshot_dir/scripts/artifact-manifest.sh"
ghelper_write_artifact_manifest \
    "$snapshot_dir/dist" "$snapshot_version" "$snapshot_source_provenance" \
    "$snapshot_mode" "$IMAGE_ID" "$IMAGE_INPUT_HASH" "$CACHE_INPUT_HASH" \
    "$NUGET_MANIFEST_HASH" "$ENVIRONMENT_HASH"

output_parent="$(dirname "$OUTPUT_DIR")"
mkdir -p "$output_registry_root"
chmod 700 "$user_cache_root/ghelper-x13-build" "$output_registry_root"
if [[ "$output_was_existing" == "0" ]]; then
    output_nonce="$(od -An -tx1 -N32 /dev/urandom | tr -d ' \n')"
    [[ "$output_nonce" =~ ^[0-9a-f]{64}$ ]] || exit 1
    registry_stage="$(mktemp "$output_registry_root/.owner.XXXXXX")"
    owned_marker_text > "$registry_stage"
    chmod 600 "$registry_stage"
    mv -- "$registry_stage" "$output_registry"
fi

output_stage="$(mktemp -d "$output_parent/.ghelper-output.XXXXXX")"
chmod 700 "$output_stage"
owned_marker_text > "$output_stage/$OUTPUT_MARKER_NAME"
chmod 600 "$output_stage/$OUTPUT_MARKER_NAME"
cp -a -- "$snapshot_dir/dist/." "$output_stage/"
chmod 700 "$output_stage"
if [[ -e "$OUTPUT_DIR" ]]; then
    output_backup="$output_parent/.ghelper-previous.$output_nonce"
    [[ ! -e "$output_backup" && ! -L "$output_backup" ]] || {
        echo "ERROR: refusing occupied atomic-output backup path" >&2
        exit 2
    }
    mv -- "$OUTPUT_DIR" "$output_backup"
    if ! mv -- "$output_stage" "$OUTPUT_DIR"; then
        mv -- "$output_backup" "$OUTPUT_DIR"
        exit 1
    fi
    safe_delete_owned_output "$output_backup"
else
    mv -- "$output_stage" "$OUTPUT_DIR"
fi
printf 'Published unsigned reproducible output: %s\n' "$OUTPUT_DIR"
