#!/usr/bin/env bash
set -euo pipefail

# G-Helper Linux public build entrypoint. Outside the wrapper-owned container
# context this file can only delegate to the isolated snapshot builder.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_CONTEXT=/tmp/ghelper-build-context
BUILD_TOKEN=/tmp/ghelper-build-token
if [[ ! -e "$BUILD_CONTEXT" && ! -e "$BUILD_TOKEN" ]]; then
    exec "$SCRIPT_DIR/scripts/build-container.sh" "$@"
fi

exact_readonly_mount() {
    local path="$1" record target options
    record="$(findmnt -n -M "$path" -o TARGET,OPTIONS 2>/dev/null)" || return 1
    [[ "$record" != *$'\n'* ]] || return 1
    read -r target options <<< "$record"
    [[ "$target" == "$path" ]] || return 1
    case ",${options}," in *,ro,*) ;; *) return 1 ;; esac
    case ",${options}," in *,rw,*) return 1 ;; esac
}

if ! command -v findmnt >/dev/null 2>&1 \
   || ! exact_readonly_mount "$BUILD_CONTEXT" \
   || ! exact_readonly_mount "$BUILD_TOKEN" \
   || [[ ! -f "$BUILD_CONTEXT" || -L "$BUILD_CONTEXT" \
      || ! -f "$BUILD_TOKEN" || -L "$BUILD_TOKEN" ]] \
   || [[ "$(stat -c %u -- "$BUILD_CONTEXT")" != "$(id -u)" \
      || "$(stat -c %a -- "$BUILD_CONTEXT")" != "400" \
      || "$(stat -c %u -- "$BUILD_TOKEN")" != "$(id -u)" \
      || "$(stat -c %a -- "$BUILD_TOKEN")" != "400" ]]; then
    echo "ERROR: internal compilation requires a wrapper-owned read-only context." >&2
    exit 1
fi

SRC_DIR="$SCRIPT_DIR/src"
DAEMON_DIR="$SCRIPT_DIR/daemon"
DIST_DIR="$SCRIPT_DIR/dist"
PUBLISH_DIR="$SRC_DIR/bin/Release/net10.0/linux-x64/publish"
DAEMON_PUBLISH_DIR="$DAEMON_DIR/bin/Release/net10.0/linux-x64/publish"

caller_provenance="${GHELPER_BUILD_PROVENANCE:-}"
caller_version="${GHELPER_INFORMATIONAL_VERSION:-}"
unset GHELPER_BUILD_MODE GHELPER_DIFF_HASH GHELPER_BUILD_PROVENANCE \
    GHELPER_INFORMATIONAL_VERSION GHELPER_BUILD_ENV_HASH

context_value() {
    local key="$1"
    awk -F= -v wanted="$key" '$1 == wanted { sub(/^[^=]*=/, ""); print; found++ }
        END { if (found != 1) exit 1 }' "$BUILD_CONTEXT"
}

CONTEXT_FORMAT="$(context_value format)"
CONTEXT_WRAPPER_NONCE="$(context_value wrapper_nonce)"
[[ "$CONTEXT_FORMAT" == "ghelper-phase1-build-context-v1" ]] || {
    echo "ERROR: invalid wrapper build context format." >&2
    exit 1
}
[[ "$CONTEXT_WRAPPER_NONCE" =~ ^[0-9a-f]{64}$ \
   && "$(cat -- "$BUILD_TOKEN")" == \
      "ghelper-phase1-wrapper-v1:$CONTEXT_WRAPPER_NONCE" ]] || {
    echo "ERROR: wrapper handshake token does not match the build context." >&2
    exit 1
}
CONTEXT_SOURCE_PROVENANCE="$(context_value source_provenance)"
CONTEXT_BUILD_MODE="$(context_value build_mode)"
CONTEXT_VERSION="$(context_value informational_version)"
CONTEXT_IMAGE_ID="$(context_value image_id)"
CONTEXT_IMAGE_INPUT_HASH="$(context_value image_input_sha256)"
CONTEXT_CACHE_INPUT_HASH="$(context_value cache_input_sha256)"
CONTEXT_NUGET_MANIFEST_HASH="$(context_value nuget_manifest_sha256)"
CONTEXT_ENV_HASH="$(context_value environment_sha256)"
[[ "$CONTEXT_IMAGE_ID" =~ ^sha256:[0-9a-f]{64}$ \
   && "$CONTEXT_IMAGE_INPUT_HASH" =~ ^[0-9a-f]{64}$ \
   && "$CONTEXT_CACHE_INPUT_HASH" =~ ^[0-9a-f]{64}$ \
   && "$CONTEXT_NUGET_MANIFEST_HASH" =~ ^[0-9a-f]{64}$ \
   && "$CONTEXT_ENV_HASH" =~ ^[0-9a-f]{64}$ ]] || {
    echo "ERROR: malformed wrapper reproducibility context." >&2
    exit 1
}
[[ "$CONTEXT_BUILD_MODE" == "clean" \
   || "$CONTEXT_BUILD_MODE" == "dirty-review" ]] || {
    echo "ERROR: malformed wrapper build mode." >&2
    exit 1
}

# shellcheck source=scripts/provenance.sh
source "$SCRIPT_DIR/scripts/provenance.sh"
if [[ "$CONTEXT_BUILD_MODE" == "dirty-review" ]]; then
    GHELPER_DIRTY_REVIEW=1 ghelper_set_provenance "$SCRIPT_DIR" "$CONTEXT_ENV_HASH"
else
    GHELPER_DIRTY_REVIEW=0 ghelper_set_provenance "$SCRIPT_DIR" "$CONTEXT_ENV_HASH"
fi
[[ "$GHELPER_BUILD_PROVENANCE" == "$CONTEXT_SOURCE_PROVENANCE" \
   && "$GHELPER_INFORMATIONAL_VERSION" == "$CONTEXT_VERSION" ]] || {
    echo "ERROR: wrapper context does not describe this source snapshot." >&2
    exit 1
}
if [[ -n "$caller_provenance$caller_version" ]]; then
    echo "Ignoring caller-supplied provenance metadata; recomputed from Git state." >&2
fi
printf 'Build provenance: %s (%s)\n' "$GHELPER_BUILD_PROVENANCE" "$GHELPER_BUILD_MODE"

USE_AOT=1
while [[ $# -gt 0 ]]; do
    case "$1" in
        --no-aot|--fast|-f)
            USE_AOT=0
            ;;
        --print-provenance)
            printf '%s\n' "$GHELPER_INFORMATIONAL_VERSION"
            exit 0
            ;;
        -h|--help)
            cat <<EOF
Usage: $0 [--no-aot|--fast|-f]

Modes:
  (default)            Full Native AOT build. Single native binary
                       in dist/ghelper. Takes ~2 min.
  --no-aot, --fast,-f  Fast iteration build. Skips AOT/trimming. dist/
                       becomes a folder (~80MB) containing ghelper + DLLs.
                       Incremental rebuilds (~5-10s after first run).

Other flags:
  --print-provenance   Recompute and print exact Git/diff provenance.
  -h, --help           Show this message.
EOF
            exit 0
            ;;
        *)
            echo "Unknown flag: $1" >&2
            echo "Run '$0 --help' for usage." >&2
            exit 2
            ;;
    esac
    shift
done

echo "=== G-Helper Linux Build ==="
if (( USE_AOT )); then
    echo "    Mode: Native AOT"
else
    echo "    Mode: Fast (no AOT, no trim)"
fi
echo ""

# Check .NET SDK
if ! command -v dotnet &>/dev/null; then
    echo "ERROR: .NET SDK not found."
    echo "This tree requires exactly .NET SDK 10.0.400."
    echo "Use ./scripts/build-container.sh instead of installing a floating host SDK."
    exit 1
fi

SDK_VERSION=$(dotnet --version 2>/dev/null || echo "unknown")
echo "Using .NET SDK: $SDK_VERSION"
if [[ "$SDK_VERSION" != "10.0.400" ]]; then
    echo "ERROR: This source tree requires exactly .NET SDK 10.0.400." >&2
    echo "Use ./scripts/build-container.sh when that SDK is not installed." >&2
    exit 1
fi

NUGET_DIR="${NUGET_PACKAGES:-}"
[[ -n "$NUGET_DIR" && "$NUGET_DIR" == /* ]] || {
    echo "ERROR: internal build requires an absolute private NuGet cache." >&2
    exit 1
}
# shellcheck source=scripts/cache-manifest.sh
source "$SCRIPT_DIR/scripts/cache-manifest.sh"
actual_nuget_manifest_hash="$(ghelper_nuget_manifest_sha256 "$NUGET_DIR")"
[[ "$actual_nuget_manifest_hash" == "$CONTEXT_NUGET_MANIFEST_HASH" ]] || {
    echo "ERROR: private NuGet cache does not match wrapper reproducibility context." >&2
    exit 1
}

# Check for clang (required for AOT)
if ! command -v clang &>/dev/null; then
    echo ""
    echo "WARNING: clang not found. Native AOT requires clang."
    echo "Install it with:"
    echo "  Ubuntu/Debian:  sudo apt install clang zlib1g-dev"
    echo "  Fedora:         sudo dnf install clang zlib-devel"
    echo "  Arch:           sudo pacman -S clang"
    echo ""
    read -rp "Try building anyway? [y/N] " ans
    [[ "$ans" =~ ^[Yy] ]] || exit 1
fi

# Build ghelper-audio (PipeWire audio helper for noise suppression + DSP chain).
# Compiled as a tiny native helper, embedded into the AOT binary, extracted
# at runtime by NativeLibExtractor. Vendored rnnoise (BSD-3) is GPL-3 compatible.
AUDIO_HELPER_DIR="$SCRIPT_DIR/audio-helper"
AUDIO_HELPER_BIN=""
WLR_RANDR_DIR="$SCRIPT_DIR/vendor/wlr-randr"

safe_delete_generated_dir() {
    local candidate="$1"
    case "$candidate" in
        "$SRC_DIR/bin"|"$SRC_DIR/obj"|"$SRC_DIR/bin/Release"|"$SRC_DIR/obj/Release"|\
        "$DAEMON_DIR/bin"|"$DAEMON_DIR/obj"|\
        "$SCRIPT_DIR/build/embedded"|"$DIST_DIR") ;;
        *)
            echo "ERROR: refusing unexpected generated-path cleanup: $candidate" >&2
            return 1
            ;;
    esac
    [[ ! -L "$candidate" ]] || {
        echo "ERROR: refusing symlinked generated-path cleanup: $candidate" >&2
        return 1
    }
    if [[ -e "$candidate" ]]; then
        [[ -d "$candidate" ]] || {
            echo "ERROR: generated cleanup target is not a directory: $candidate" >&2
            return 1
        }
        find -P "$candidate" -depth -delete
    fi
}

cleanup_generated_build_files() {
    rm -f "$WLR_RANDR_DIR/wlr-randr" \
          "$WLR_RANDR_DIR/wlr-output-management-unstable-v1-client-protocol.h" \
          "$WLR_RANDR_DIR/wlr-output-management-unstable-v1-protocol.c"
    (cd "$AUDIO_HELPER_DIR" && make clean >/dev/null 2>&1) || true
    safe_delete_generated_dir "$SCRIPT_DIR/build/embedded" || true
}
trap cleanup_generated_build_files EXIT

if ! command -v pkg-config &>/dev/null || \
   ! pkg-config --exists libpipewire-0.3 2>/dev/null || \
   ! command -v cc &>/dev/null; then
    echo ""
    echo "ERROR: Cannot build ghelper-audio — libpipewire-0.3 dev headers not found."
    echo "  Install with:"
    echo "    Ubuntu/Debian: sudo apt install libpipewire-0.3-dev pkg-config"
    echo "    Fedora:        sudo dnf install pipewire-devel pkg-config"
    echo "    Arch:          sudo pacman -S libpipewire pkg-config"
    exit 1
fi

echo ""
echo "Building ghelper-audio (PipeWire helper)..."
(
    cd "$AUDIO_HELPER_DIR"
    make clean >/dev/null 2>&1
    make -j"$(nproc 2>/dev/null || echo 2)"
)
if [[ -f "$AUDIO_HELPER_DIR/ghelper-audio" ]]; then
    AUDIO_HELPER_BIN="$AUDIO_HELPER_DIR/ghelper-audio"
    echo "  ghelper-audio built: $(du -sh "$AUDIO_HELPER_BIN" | cut -f1)"
else
    echo ""
    echo "ERROR: ghelper-audio build failed — see make output above."
    exit 1
fi

# Build wlr-randr (Wayland display tool — vendored v0.5.0, MIT license)
WLR_RANDR_BIN=""

if command -v wayland-scanner &>/dev/null && command -v cc &>/dev/null; then
    WLR_VERSION=$(cat "$WLR_RANDR_DIR/VERSION" 2>/dev/null || echo "unknown")
    echo ""
    echo "Building wlr-randr v${WLR_VERSION}..."
    (
        cd "$WLR_RANDR_DIR"
        wayland-scanner client-header \
            protocol/wlr-output-management-unstable-v1.xml \
            wlr-output-management-unstable-v1-client-protocol.h
        wayland-scanner private-code \
            protocol/wlr-output-management-unstable-v1.xml \
            wlr-output-management-unstable-v1-protocol.c
        cc -O2 -o wlr-randr main.c wlr-output-management-unstable-v1-protocol.c \
            -I. -lwayland-client -lm
        strip wlr-randr
    )
    if [[ -f "$WLR_RANDR_DIR/wlr-randr" ]]; then
        WLR_RANDR_BIN="$WLR_RANDR_DIR/wlr-randr"
        echo "  wlr-randr built: $(du -sh "$WLR_RANDR_BIN" | cut -f1)"
    else
        echo "WARNING: wlr-randr build failed (Wayland refresh rate switching unavailable)"
    fi
else
    echo ""
    echo "NOTE: wayland-scanner not found, skipping wlr-randr build."
    echo "  Install with: sudo apt install libwayland-dev"
fi

# Clean all main-project intermediates. AOT review/release builds must never
# inherit generated state from a previous checkout or configuration.
echo ""
if (( USE_AOT )); then
    echo "[1/4] Cleaning previous build..."
    safe_delete_generated_dir "$SRC_DIR/bin"
    safe_delete_generated_dir "$SRC_DIR/obj"
else
    echo "[1/4] Cleaning (fast mode) to force MSBuild condition re-evaluation..."
    safe_delete_generated_dir "$SRC_DIR/bin/Release"
    safe_delete_generated_dir "$SRC_DIR/obj/Release"
fi
safe_delete_generated_dir "$DAEMON_DIR/bin"
safe_delete_generated_dir "$DAEMON_DIR/obj"

# Restore packages
echo "[2/4] Restoring packages..."
if ! dotnet restore "$SRC_DIR" --runtime linux-x64 --locked-mode -q; then
    echo "ERROR: Package restore failed."
    exit 1
fi
if ! dotnet restore "$DAEMON_DIR" --runtime linux-x64 --locked-mode -q; then
    echo "ERROR: Daemon package restore failed."
    exit 1
fi

# Prepare native .so for embedding
EMBED_DIR="$SCRIPT_DIR/build/embedded"
safe_delete_generated_dir "$EMBED_DIR"
mkdir -p "$EMBED_DIR"
printf '%s\n' "$GHELPER_INFORMATIONAL_VERSION" > "$EMBED_DIR/provenance.txt"
awk '!/^wrapper_nonce=/' "$BUILD_CONTEXT" > "$EMBED_DIR/build-environment.txt"

LOCK_FILE="$SRC_DIR/packages.lock.json"

locked_package_version() {
    local package="$1"
    local versions=()
    mapfile -t versions < <(
        awk -v key="\"$package\"" '
            index($0, key ": {") { in_package = 1; next }
            in_package && /"resolved"[[:space:]]*:/ {
                value = $0
                sub(/^.*"resolved"[[:space:]]*:[[:space:]]*"/, "", value)
                sub(/".*$/, "", value)
                print value
                in_package = 0
            }
        ' "$LOCK_FILE" | sort -u
    )
    if [[ "${#versions[@]}" -ne 1 || ! "${versions[0]}" =~ ^[0-9A-Za-z.+-]+$ ]]; then
        echo "ERROR: expected one locked version for $package, found: ${versions[*]:-none}" >&2
        return 1
    fi
    printf '%s\n' "${versions[0]}"
}

SKIA_NATIVE_VERSION="$(locked_package_version "SkiaSharp.NativeAssets.Linux")"
HARFBUZZ_NATIVE_VERSION="$(locked_package_version "HarfBuzzSharp.NativeAssets.Linux")"
for lib_spec in \
    "libSkiaSharp.so:skiasharp.nativeassets.linux:$SKIA_NATIVE_VERSION:runtimes/linux-x64/native/libSkiaSharp.so" \
    "libHarfBuzzSharp.so:harfbuzzsharp.nativeassets.linux:$HARFBUZZ_NATIVE_VERSION:runtimes/linux-x64/native/libHarfBuzzSharp.so"; do
    IFS=':' read -r lib_name pkg_name pkg_version pkg_path <<< "$lib_spec"
    pkg_dir="$NUGET_DIR/$pkg_name/$pkg_version"
    if [[ ! -f "$pkg_dir/$pkg_path" ]]; then
        echo "ERROR: Locked native asset missing: $pkg_name/$pkg_version/$pkg_path" >&2
        exit 1
    fi
    cp "$pkg_dir/$pkg_path" "$EMBED_DIR/$lib_name"
    strip --strip-unneeded "$EMBED_DIR/$lib_name" 2>/dev/null || true
    echo "  Embedded $lib_name: $(du -sh "$EMBED_DIR/$lib_name" | cut -f1) (stripped)"
done

# Embed ghelper-audio helper if it was built
if [[ -n "$AUDIO_HELPER_BIN" && -f "$AUDIO_HELPER_BIN" ]]; then
    cp "$AUDIO_HELPER_BIN" "$EMBED_DIR/ghelper-audio"
    echo "  Embedded ghelper-audio: $(du -sh "$EMBED_DIR/ghelper-audio" | cut -f1)"
fi

# Publish
if (( USE_AOT )); then
    echo "[3/4] Compiling native AOT binary (this may take a minute)..."
    dotnet publish "$SRC_DIR" -c Release --no-restore \
        -p:GHelperCanonicalLocalBuild=true \
        -p:InformationalVersion="$GHELPER_INFORMATIONAL_VERSION" \
        -p:SourceRevisionId="$GHELPER_BUILD_PROVENANCE" \
        -p:GHelperBuildMode="$GHELPER_BUILD_MODE" \
        -p:GHelperBuildImageId="$CONTEXT_IMAGE_ID" \
        -p:GHelperImageInputSha256="$CONTEXT_IMAGE_INPUT_HASH" \
        -p:GHelperCacheInputSha256="$CONTEXT_CACHE_INPUT_HASH" \
        -p:GHelperNuGetManifestSha256="$CONTEXT_NUGET_MANIFEST_HASH" \
        -p:GHelperBuildEnvironmentSha256="$CONTEXT_ENV_HASH"
else
    echo "[3/4] Compiling (fast mode, no AOT)..."
    dotnet publish "$SRC_DIR" -c Release --no-restore \
        -p:PublishAot=false \
        -p:PublishTrimmed=false \
        -p:StripSymbols=false \
        -p:GHelperCanonicalLocalBuild=true \
        -p:InformationalVersion="$GHELPER_INFORMATIONAL_VERSION" \
        -p:SourceRevisionId="$GHELPER_BUILD_PROVENANCE" \
        -p:GHelperBuildMode="$GHELPER_BUILD_MODE" \
        -p:GHelperBuildImageId="$CONTEXT_IMAGE_ID" \
        -p:GHelperImageInputSha256="$CONTEXT_IMAGE_INPUT_HASH" \
        -p:GHelperCacheInputSha256="$CONTEXT_CACHE_INPUT_HASH" \
        -p:GHelperNuGetManifestSha256="$CONTEXT_NUGET_MANIFEST_HASH" \
        -p:GHelperBuildEnvironmentSha256="$CONTEXT_ENV_HASH" \
        --self-contained true -r linux-x64
fi

echo "    Compiling ghelperd native helper..."
dotnet publish "$DAEMON_DIR" -c Release --no-restore \
    -p:GHelperCanonicalDaemonBuild=true

# Verify the binary was produced
if [[ ! -f "$PUBLISH_DIR/ghelper" ]]; then
    echo ""
    echo "ERROR: Build failed — binary not found at $PUBLISH_DIR/ghelper"
    echo "Run 'dotnet publish src/ -c Release' manually to see full errors."
    exit 1
fi
if [[ ! -f "$DAEMON_PUBLISH_DIR/ghelperd" ]]; then
    echo "ERROR: daemon build failed - binary not found at $DAEMON_PUBLISH_DIR/ghelperd" >&2
    exit 1
fi

# Copy to dist/
echo "[4/4] Copying to dist/..."
safe_delete_generated_dir "$DIST_DIR"
mkdir -p "$DIST_DIR"

if (( USE_AOT )); then
    cp "$PUBLISH_DIR/ghelper" "$DIST_DIR/"
else
    cp -r "$PUBLISH_DIR/." "$DIST_DIR/"
fi
chmod +x "$DIST_DIR/ghelper"
mkdir -p "$DIST_DIR/system"
cp "$DAEMON_PUBLISH_DIR/ghelperd" "$DIST_DIR/system/ghelperd"
chmod +x "$DIST_DIR/system/ghelperd"

# Summary
BINARY_SIZE=$(du -sh "$DIST_DIR/ghelper" | cut -f1)
TOTAL_SIZE=$(du -sh "$DIST_DIR" | cut -f1)
FILE_COUNT=$(ls -1 "$DIST_DIR" | wc -l)

echo ""
echo "=== Build Complete ==="
if (( USE_AOT )); then
    echo "  Mode:    Native AOT (single binary)"
else
    echo "  Mode:    Fast (no AOT, folder output)"
fi
echo "  Binary:  $BINARY_SIZE  (ghelper)"
echo "  Total:   $TOTAL_SIZE  ($FILE_COUNT files)"
echo "  Output:  $DIST_DIR/"
echo "  Daemon:  $DIST_DIR/system/ghelperd"
echo ""
echo "Run it:"
echo "  $DIST_DIR/ghelper"
