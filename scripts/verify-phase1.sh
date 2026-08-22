#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ENGINE="${CONTAINER_ENGINE:-docker}"
container_user_args=(--user "$(id -u):$(id -g)")
if [[ "$(basename -- "$ENGINE")" == "podman" ]]; then
    container_user_args=(--userns=keep-id --user "$(id -u):$(id -g)")
fi
WORK_DIR="$(mktemp -d -t ghelper-phase1.XXXXXX)"
VERIFY_SOURCE="$WORK_DIR/reviewed-source"
VERIFY_CACHE_HOME="$WORK_DIR/xdg-cache"
MUTATION_CACHE_HOME="$WORK_DIR/mutation-xdg-cache"
# shellcheck source=provenance.sh
source "$SCRIPT_DIR/provenance.sh"
# shellcheck source=cache-manifest.sh
source "$SCRIPT_DIR/cache-manifest.sh"

cleanup() {
    if [[ "$WORK_DIR" == /tmp/ghelper-phase1.* && -d "$WORK_DIR" ]]; then
        rm -rf -- "$WORK_DIR"
    fi
}
trap cleanup EXIT
cd "$REPO_DIR"

fail() {
    echo "ERROR: $*" >&2
    exit 1
}

lock_base="${XDG_RUNTIME_DIR:-/tmp}"
[[ "$lock_base" == /* && -d "$lock_base" ]] || lock_base=/tmp
repo_lock_key="$(printf '%s' "$REPO_DIR" | sha256sum | cut -d' ' -f1)"
exec 9>"$lock_base/ghelper-phase1-${repo_lock_key}.lock"
flock -n 9 || fail "another Phase 1 verifier is already running for this repository"

materialize_source_snapshot() {
    local source_repo="$1" destination="$2" patch_file="$WORK_DIR/reviewed.patch"
    local head
    head="$(git -C "$source_repo" rev-parse HEAD)"
    git -c init.defaultBranch=x13-hardened clone --quiet --no-hardlinks --no-tags \
        "$source_repo" "$destination"
    git -c advice.detachedHead=false -C "$destination" \
        checkout --quiet --detach "$head"
    git -C "$source_repo" diff --binary --full-index --no-ext-diff HEAD -- > "$patch_file"
    if [[ -s "$patch_file" ]]; then
        git -C "$destination" apply --binary "$patch_file"
    fi
    while IFS= read -r -d '' path; do
        mkdir -p "$destination/$(dirname "$path")"
        cp -a -- "$source_repo/$path" "$destination/$path"
    done < <(git -C "$source_repo" ls-files --others --exclude-standard -z \
        | LC_ALL=C sort -z)
}

tree_snapshot() {
    local root="$1"
    {
        find "$root" -mindepth 1 -printf '%P|%y|%m|%s\n' | LC_ALL=C sort
        while IFS= read -r -d '' path; do
            printf '%s|' "${path#"$root"/}"
            sha256sum "$path" | cut -d' ' -f1
        done < <(find "$root" -type f -print0 | LC_ALL=C sort -z)
    } | sha256sum | cut -d' ' -f1
}

reviewed_checkout_snapshot() {
    tar --sort=name --format=posix \
        --mtime='UTC 1970-01-01' --owner=0 --group=0 --numeric-owner \
        --pax-option=delete=atime,delete=ctime \
        --exclude='./.git' -cf - -C "$REPO_DIR" . \
        | sha256sum | cut -d' ' -f1
}

assert_no_generated_vendor_outputs() {
    local forbidden=(
        vendor/wlr-randr/wlr-randr
        vendor/wlr-randr/wlr-output-management-unstable-v1-client-protocol.h
        vendor/wlr-randr/wlr-output-management-unstable-v1-protocol.c
        vendor/gpu-helper/gpu-helper
    )
    local path
    for path in "${forbidden[@]}"; do
        [[ ! -e "$path" ]] || fail "generated vendor output remains: $path"
    done
    [[ -z "$(git ls-files --others --exclude-standard vendor)" ]] \
        || fail "untracked vendor files found"

    mapfile -t vendor_executables < <(find vendor -type f -perm /111 -print | sort)
    [[ "${#vendor_executables[@]}" == "0" ]] \
        || fail "executable found under vendor/: ${vendor_executables[*]}"
}

assert_install_quarantine() {
    local executable_install_inputs=()
    mapfile -t executable_install_inputs < <(find install -type f -perm /111 -print | sort)
    [[ "${#executable_install_inputs[@]}" == "0" ]] \
        || fail "quarantined install input is executable: ${executable_install_inputs[*]}"
}

assert_no_ignored_build_outputs() {
    local candidate
    for candidate in \
        .build-cache \
        build \
        dist/ghelper \
        src/bin \
        src/obj \
        daemon/bin \
        daemon/obj \
        tests/GHelper.Linux.Tests/bin \
        tests/GHelper.Linux.Tests/obj \
        audio-helper/tests/cs/bin \
        audio-helper/tests/cs/obj; do
        [[ ! -e "$candidate" && ! -L "$candidate" ]] \
            || fail "ignored compiled output remains in reviewed checkout: $candidate"
    done
}

head_commit="$(git rev-parse HEAD)"
git merge-base --is-ancestor "$GHELPER_UPSTREAM_COMMIT" "$head_commit" \
    || fail "reviewed upstream v1.0.90 is not an ancestor of HEAD"
case "$(git branch --show-current)" in
    x13-hardened|reference/gv301qh-xg-mobile) ;;
    *) fail "unexpected branch" ;;
esac
ghelper_set_provenance "$REPO_DIR"
expected_provenance="$GHELPER_BUILD_PROVENANCE"
review_mode="$GHELPER_BUILD_MODE"
reviewed_guard_status="$(git status --porcelain=v1 --untracked-files=all)"
reviewed_guard_hash="$(ghelper_worktree_diff_hash "$REPO_DIR")"
reviewed_checkout_hash="$(reviewed_checkout_snapshot)"
assert_no_ignored_build_outputs
printf 'Acceptance provenance: %s (%s)\n' "$expected_provenance" "$review_mode"
if [[ "$review_mode" == "dirty-review" ]]; then
    : # Exercised below after the verifier-private cache snapshot is prepared.
fi
unset GHELPER_BUILD_MODE GHELPER_DIFF_HASH GHELPER_BUILD_PROVENANCE \
    GHELPER_INFORMATIONAL_VERSION GHELPER_BUILD_ENV_HASH

[[ ! -e build-appimage.sh ]] || fail "AppImage build path must stay disabled"
[[ ! -e scripts/update-ryzenadj.sh ]] || fail "floating RyzenAdj updater must stay disabled"
[[ ! -e vendor/wlr-randr/update.sh ]] || fail "floating wlr-randr updater must stay disabled"
if find nixos -type f -print -quit 2>/dev/null | grep -q .; then
    fail "unsafe upstream NixOS package/module path must stay disabled"
fi
mapfile -t workflows < <(find .github/workflows -type f -print 2>/dev/null | sort)
[[ "${#workflows[@]}" == "1" && "${workflows[0]}" == ".github/workflows/verify-pr.yml" ]] \
    || fail "only the hardened PR verification workflow may exist"
rg -q 'actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683' \
    .github/workflows/verify-pr.yml || fail "checkout action is not commit-pinned"
rg -q 'fetch-depth:[[:space:]]*0' .github/workflows/verify-pr.yml \
    || fail "PR workflow cannot validate the immutable upstream base"
rg -q 'persist-credentials:[[:space:]]*false' .github/workflows/verify-pr.yml \
    || fail "PR workflow retains push credentials"
! rg -q '^[[:space:]]*(push|workflow_dispatch|release):|upload-artifact|installer|AppImage' \
    .github/workflows/verify-pr.yml || fail "PR workflow contains a publication/unsafe path"
rg -q 'run: ./scripts/verify-phase1\.sh' .github/workflows/verify-pr.yml \
    || fail "PR workflow does not run the exact acceptance entrypoint"
! rg -q 'git remote rename origin' .github/workflows/verify-pr.yml \
    || fail "PR workflow renames the checkout remote"
rg -q 'git remote add upstream https://github\.com/utajum/g-helper-linux\.git' \
    .github/workflows/verify-pr.yml || fail "PR workflow does not configure immutable upstream provenance"
[[ "$(rg -c 'git remote set-url --push (origin|upstream) no_push://disabled' \
    .github/workflows/verify-pr.yml)" == "2" ]] \
    || fail "PR workflow must disable push to both checkout and upstream remotes"
rg -q 'run: ./scripts/prepare-ci\.sh' .github/workflows/verify-pr.yml \
    || fail "PR workflow does not use the safe runner preparation entrypoint"
! rg -q '/tmp/ghelper|--output[[:space:]]+/tmp' .github/workflows/verify-pr.yml \
    || fail "PR workflow publishes build output beneath a broad /tmp parent"
rg -q 'mktemp -d "\$runner_temp/ghelper-phase1-prepare\.XXXXXX"' scripts/prepare-ci.sh \
    || fail "CI preparation does not create a private RUNNER_TEMP child"
rg -q 'chmod 700 "\$ci_root"' scripts/prepare-ci.sh \
    || fail "CI preparation directory is not mode 0700"
rg -q 'XDG_CACHE_HOME: \$\{\{ runner\.temp \}\}/ghelper-phase1-cache' \
    .github/workflows/verify-pr.yml \
    || fail "PR workflow does not use its private runner cache"
rg -q 'XDG_CACHE_HOME="\$ci_cache"' scripts/prepare-ci.sh \
    || fail "CI preparation does not use its validated runner cache"
assert_no_generated_vendor_outputs

rg -q '<Version>1\.0\.90-x13\.1</Version>' src/GHelper.Linux.csproj \
    || fail "fork version is not pinned"
rg -q '<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>' \
    src/GHelper.Linux.csproj \
    || fail "SDK source revision suffixing can duplicate informational metadata"
! rg -q 'EmbeddedResource Include="\.\./install/|EmbeddedResource Include="\.\./vendor/gpu-helper' \
    src/GHelper.Linux.csproj || fail "unsafe installer/helper payload remains embedded"
! rg -q 'LogicalName="ryzenadj"|vendor/ryzenadj/ryzenadj' src/GHelper.Linux.csproj build.sh \
    || fail "RyzenAdj remains in the application build"
[[ ! -e src/Install/Installer.Diff.cs ]] \
    || fail "upstream installer diff implementation must stay removed"
! rg -q 'NOPASSWD|FileName[[:space:]]*=[[:space:]]*"pkexec"|chmod 0666' src/Install \
    || fail "privileged upstream installer implementation remains compiled"
! sed -n '/private async void ButtonSysFilesUninstall_Click/,/private ChangelogWindow/p' \
    src/UI/Views/UpdatesWindow.axaml.cs | grep -q 'SteamShortcuts.Remove' \
    || fail "disabled uninstall path can still remove the Steam shortcut"
rg -q 'rm -rf "\$CS_DIR/bin" "\$CS_DIR/obj/Debug"' audio-helper/tests/09_csharp_parser.sh \
    || fail "audio parser test does not clean current intermediates"
rg -q 'dotnet build .*--no-restore' audio-helper/tests/09_csharp_parser.sh \
    || fail "audio parser test is not an explicit no-restore source build"
! rg -q 'if \[\[ ! -f "\$BIN"' audio-helper/tests/09_csharp_parser.sh \
    || fail "audio parser test may reuse a stale binary"

rg -q 'public const string GpuHelperPath = ""' src/Platform/Linux/SysfsHelper.cs \
    || fail "gpu-helper path discovery is not fail-closed"
rg -q 'public const string RyzenadjPath = ""' src/Platform/Linux/SysfsHelper.cs \
    || fail "RyzenAdj path discovery is not fail-closed"
! rg -q 'File\.Exists\([^\n]*(gpu-helper|ryzenadj)|WhichCached\("(gpu-helper|gpu-block-helper\.sh|ryzenadj)' \
    src/Gpu/NVidia/NvidiaProcessScanner.cs src/Platform/Linux/SysfsHelper.cs \
    src/Platform/Linux/NixOS.cs src/Gpu/GPUModeControl.cs \
    || fail "legacy external privileged-helper discovery remains"
! rg -q 'RunCommandWithTimeout\(SysfsHelper\.SudoPath|FileName = SysfsHelper\.SudoPath|RunCommandWithTimeout\("pkexec"' \
    src/Gpu/NVidia/NvidiaProcessScanner.cs src/Gpu/GPUModeControl.cs src/Platform/Linux/SysfsHelper.cs \
    || fail "legacy direct sudo/pkexec execution remains"
rg -q 'AtomicPayloadInstaller\.EnsureExact' src/Helpers/NativeLibExtractor.cs \
    || fail "native extraction does not use exact atomic validation"
rg -q 'AtomicPayloadInstaller\.ContentAddressedPath' src/Helpers/NativeLibExtractor.cs \
    || fail "native extraction is not keyed by exact embedded content"
rg -q 'AbsoluteUserPaths\.CachePath\("ghelper", "libs"\)' \
    src/Helpers/NativeLibExtractor.cs \
    || fail "native cache does not use the central absolute-path resolver"
! rg -q 'using cached copy|resolved via PATH|falling back to stale|NativeLibrary\.Load\(lib\)' \
    src/Helpers/NativeLibExtractor.cs || fail "native extraction retains an unverified fallback"
! rg -q 'Labels\.Initialize|AppConfig\.' src/Cli/ResourceExtractorCli.cs \
    || fail "disabled helper CLI initializes persistent application state"
rg -q 'ResourceExtractorCli\.TryDispatch\(args\)' src/Program.cs \
    || fail "early CLI dispatcher is missing"
[[ "$(rg -n 'ResourceExtractorCli\.TryDispatch\(args\)|Cosmic\.ImportSessionEnvironment|SetGpuPreferenceEnv\(' \
    src/Program.cs | head -n 1)" == *'ResourceExtractorCli.TryDispatch(args)'* ]] \
    || fail "disabled helper CLI is not dispatched before application initialization"
rg -q 'args\.Length == 1 && args\[0\] == "--print-build-metadata"' \
    src/Cli/ResourceExtractorCli.cs \
    || fail "metadata CLI does not require exact arity"
rg -q 'return 64;' src/Cli/ResourceExtractorCli.cs \
    || fail "normal runtime launch is not refused with EX_USAGE"
! rg -q 'skip_update_prompt' src \
    || fail "dead self-update preference remains in UI/config/i18n"
! rg -q 'HttpClient|GetByteArrayAsync|LoadImageAsync' src/UI/Views/ChangelogRenderer.cs \
    || fail "changelog rendering can still fetch remote images"

for path_consumer in \
    src/Helpers/AppConfig.cs \
    src/Helpers/CoinSound.cs \
    src/Helpers/NativeLibExtractor.cs \
    src/Platform/Linux/ImmutableOs.cs \
    src/Platform/Linux/KwinRules.cs \
    src/Platform/Linux/LinuxSystemIntegration.cs \
    src/Platform/Linux/SteamShortcuts.cs; do
    ! rg -q 'SpecialFolder\.UserProfile|XDG_(CONFIG|CACHE|DATA)_HOME' \
        "$path_consumer" \
        || fail "user filesystem path bypasses AbsoluteUserPaths in $path_consumer"
done
rg -q 'Path\.IsPathFullyQualified' src/Helpers/AbsoluteUserPaths.cs \
    || fail "central user-path resolver does not enforce absolute roots"
rg -q 'No absolute user home/profile path is available' src/Helpers/AbsoluteUserPaths.cs \
    || fail "central user-path resolver does not fail closed"
rg -q 'if \(!Path\.IsPathFullyQualified\(cacheRoot\)\)' src/Helpers/AtomicPayloadInstaller.cs \
    || fail "content-addressed cache accepts a relative root"
rg -q 'if \(!Path\.IsPathFullyQualified\(targetPath\)\)' src/Helpers/AtomicPayloadInstaller.cs \
    || fail "atomic installer accepts a relative target"

! rg -q 'File\.Exists\("/etc/udev/rules\.d/90-ghelper\.rules"\)' src/App.axaml.cs \
    || fail "legacy upstream udev file can still be accepted at startup"
rg -q 'legacy upstream udev rules:.*present but UNTRUSTED' src/Helpers/Diagnostics.cs \
    || fail "diagnostics do not label the upstream udev file untrusted"
! rg -q 'udev rules: installed|Installed udev version|expectedUdevVersion' \
    src/App.axaml.cs src/Helpers/Diagnostics.cs \
    || fail "legacy upstream udev policy is treated as trusted"

rg -q 'clone --quiet --no-hardlinks --no-tags' scripts/build-container.sh \
    || fail "container build does not create an isolated source snapshot"
rg -q 'Staged build snapshot:' scripts/build-container.sh \
    || fail "container build does not expose staged provenance"
rg -q 'staged build inputs changed during compilation' scripts/build-container.sh \
    || fail "container build lacks a post-build snapshot invariant"
! rg -q 'GHELPER_ISOLATED_SNAPSHOT' build.sh scripts/build-container.sh \
    || fail "caller-spoofable isolated-snapshot bypass remains"
rg -q 'exec "\$SCRIPT_DIR/scripts/build-container\.sh" "\$@"' build.sh \
    || fail "public build.sh does not always enter the snapshot wrapper"
rg -q 'wrapper-owned read-only context' build.sh \
    || fail "internal compiler lacks wrapper-owned context enforcement"
rg -q 'findmnt -n -M "\$path" -o TARGET,OPTIONS' build.sh \
    || fail "internal compiler does not require exact context mountpoints"
rg -q 'ghelper-phase1-wrapper-v1:' build.sh scripts/build-container.sh \
    || fail "wrapper nonce handshake is missing"
! rg -q '!= \*ro\*|--target "\$BUILD_CONTEXT"' build.sh \
    || fail "context read-only validation accepts substring/parent-mount spoofing"
rg -q 'private_nuget:/nuget:ro' scripts/build-container.sh \
    || fail "artifact build does not mount a private read-only NuGet snapshot"
[[ "$(ghelper_cache_input_files | grep -c 'packages\.lock\.json$')" == "4" ]] \
    || fail "cache identity does not cover exactly four package lock files"
for cache_input in \
    global.json Directory.Build.props NuGet.Config \
    src/GHelper.Linux.csproj src/packages.lock.json \
    daemon/GHelper.Daemon.csproj daemon/packages.lock.json \
    tests/GHelper.Linux.Tests/GHelper.Linux.Tests.csproj \
    tests/GHelper.Linux.Tests/packages.lock.json \
    audio-helper/tests/cs/TestAudioPipeline.csproj \
    audio-helper/tests/cs/packages.lock.json; do
    ghelper_cache_input_files | grep -Fxq "$cache_input" \
        || fail "cache identity omits $cache_input"
done
rg -q 'ghelper_nuget_materialize_closure.*SHARED_NUGET' scripts/build-container.sh \
    || fail "build does not prune shared cache to the exact locked closure"
rg -q 'ghelper_nuget_validate_tree "\$SHARED_NUGET"' scripts/build-container.sh \
    || fail "shared cache is not type-validated before use"
grep -Fq 'find -P "$cache_root" -mindepth 1 ! \( -type d -o -type f \)' \
    scripts/cache-manifest.sh \
    || fail "cache manifest does not reject links and special nodes"
rg -q 'private NuGet cache changed during compilation' scripts/build-container.sh \
    || fail "artifact build lacks a post-build NuGet-cache invariant"
rg -q 'os\.O_NOFOLLOW' scripts/cache-lock-exec.py \
    || fail "persistent cache path/lock opens can follow symlinks"
rg -q 'dir_fd=cache_descriptor' scripts/cache-lock-exec.py \
    || fail "cache.lock is not opened relative to the validated cache directory"
rg -q 'validate-held' scripts/build-container.sh \
    || fail "persistent cache is not revalidated after restore"
! rg -q 'exec [0-9]+>"\$CACHE_ROOT/cache\.lock"|flock [0-9]+$' \
    scripts/build-container.sh \
    || fail "shell redirection still opens the persistent cache lock"
! rg -q 'IMAGE="\$\{GHELPER_BUILD_IMAGE' scripts/build-container.sh \
    || fail "arbitrary build-image selection remains enabled"
! rg -q 'rm -rf --? "\$OUTPUT_DIR"' scripts/build-container.sh \
    || fail "build output publication can recursively delete an arbitrary destination"
rg -q 'existing output ownership validation failed' scripts/build-container.sh \
    || fail "existing build output replacement lacks protected ownership validation"
rg -q 'safe_delete_owned_output' scripts/build-container.sh \
    || fail "builder-owned output cleanup lacks a validated deletion boundary"
rg -q 'GHelper\.Linux\.BUILD_ENVIRONMENT' src/GHelper.Linux.csproj \
    || fail "complete build environment manifest is not embedded"
rg -q 'RequireCanonicalLocalReleaseBuild' src/GHelper.Linux.csproj \
    || fail "direct Release publish is not gated on canonical local inputs"
rg -q 'UNATTESTED Release publishing is disabled' src/GHelper.Linux.csproj \
    || fail "direct Release refusal is not unmistakably untrusted"
rg -q 'LOCAL-REPRODUCIBLE-UNSIGNED' src/Cli/ResourceExtractorCli.cs \
    || fail "binary metadata does not disclose its unsigned local status"
rg -q 'trust=local-reproducible-unsigned' scripts/artifact-manifest.sh \
    || fail "external manifest overstates unsigned Phase 1 trust"
rg -q 'independent local reproducible rebuild' scripts/verify-artifact.sh \
    || fail "canonical artifact verifier does not rebuild"
! rg -q '^[[:space:]]*"\$(candidate|REJECT_DIR)/ghelper"' scripts/verify-artifact.sh \
    || fail "canonical artifact verifier can execute an untrusted candidate"
rg -q '"\$rebuild_output/ghelper" --print-build-metadata' scripts/verify-artifact.sh \
    || fail "metadata execution is not deferred to the independent rebuild"
rg -q 'nuget_manifest_begin' src/Cli/ResourceExtractorCli.cs \
    || fail "metadata CLI does not validate the embedded NuGet manifest"
rg -q 'GHelperSourceProvenance' src/Cli/ResourceExtractorCli.cs src/GHelper.Linux.csproj \
    || fail "source provenance is not coherent assembly metadata"
rg -q 'GHelperBuildMode' src/Cli/ResourceExtractorCli.cs src/GHelper.Linux.csproj \
    || fail "build mode is not coherent assembly metadata"
rg -q 'component is "\." or "\.\."' src/Helpers/AbsoluteUserPaths.cs \
    || fail "central path resolver does not reject traversal components"
rg -q 'ghelper-phase1-test-sandbox-v1' install/ghelper-gpu-boot.sh \
    install/tests/test-ghelper-gpu-boot.sh \
    || fail "boot-script sandbox marker validation is missing"
rg -q 'realpath -e -- "\$ROOT/\$sandbox_tree"' install/ghelper-gpu-boot.sh \
    || fail "boot-script sandbox can escape into real /sys or /etc"
! awk '/^echo "== Audio integration tests/{active=1} active{print}' \
    scripts/verify-phase1.sh | rg -q 'runtime_dir=|--volume "\$runtime_dir|pkill' \
    || fail "verifier can attach to or terminate host audio processes"
[[ "$(printf '%s\n' '**' '!Containerfile.build' '!global.json')" == "$(cat .dockerignore)" ]] \
    || fail ".dockerignore does not enforce the minimal context"
! rg -q 'GHELPER_TEST_ROOT' src \
    || fail "production source retains an environment-controlled test-root bypass"
rg -q '#if GHELPER_TESTS' src/Gpu/GPUModeControl.cs \
    || fail "GPU controller test sandbox is not compile-time quarantined"

assert_install_quarantine
! rg -q 'EmbeddedResource Include="\.\./install/|Content Include="\.\./install/' \
    src/GHelper.Linux.csproj \
    || fail "quarantined install input remains embedded"
! rg -q 'install/(install(-local)?\.sh|ghelper-gpu-boot\.sh|gpu-block-helper\.sh|90-ghelper\.rules)' \
    src build.sh scripts --glob '!verify-phase1.sh' \
    || fail "quarantined install input remains callable from hardened code/build paths"

if rg -n -S 'releases/latest|raw\.githubusercontent\.com/.*/master|download/continuous|curl[^\n]*\|[^\n]*sudo' \
    README.md CHANGELOG.md install build.sh Containerfile.build scripts/build-container.sh \
    scripts/archive-upstream.sh src/UI/Views/UpdatesWindow.axaml.cs \
    src/UI/Views/ChangelogWindow.axaml.cs src/Platform/Linux/NixOS.cs \
    src/I18n/Languages src/App.axaml.cs; then
    fail "moving or privileged download path found in hardened runtime/build documentation"
fi
! rg -q 'RPM-managed|Install the reviewed signed RPM|re-run install script' src README.md docs \
    || fail "stale package/installer claim found"
rg -q 'blob/9e99e21153a7cf75dd1682425bf1bd3d1b1de43a/CHANGELOG\.md' \
    src/UI/Views/ChangelogWindow.axaml.cs || fail "changelog link is not commit-pinned"

bash -n build.sh scripts/build-container.sh scripts/archive-upstream.sh \
    scripts/provenance.sh scripts/cache-manifest.sh scripts/artifact-manifest.sh \
    scripts/verify-artifact.sh scripts/prepare-ci.sh "$0" \
    install/install.sh install/install-local.sh
python3 - <<'PY'
from pathlib import Path
compile(Path("scripts/cache-lock-exec.py").read_text(encoding="utf-8"),
        "scripts/cache-lock-exec.py", "exec")
PY
git diff --check

./scripts/archive-upstream.sh "$WORK_DIR/upstream.tar.gz" >/dev/null
[[ "$(stat -c %s "$WORK_DIR/upstream.tar.gz")" == "2974052" ]] \
    || fail "upstream archive size mismatch"
[[ "$(sha256sum "$WORK_DIR/upstream.tar.gz" | cut -d' ' -f1)" == \
    "0ba1f34797978af2ff245312d36a687eb5d8316b692ca93b64e7cd0f5aeab88d" ]] \
    || fail "upstream archive digest mismatch"

set +e
GHELPER_BUILD_IMAGE=forbidden-spoof \
    ./scripts/build-container.sh --print-image-id >"$WORK_DIR/image-override.log" 2>&1
override_rc=$?
set -e
[[ "$override_rc" == "1" ]] || fail "arbitrary build-image override was accepted"
grep -Fq 'overrides are forbidden' "$WORK_DIR/image-override.log" \
    || fail "build-image override refusal is not explicit"

image_id="$(GHELPER_OFFLINE=1 ./scripts/build-container.sh --print-image-id)"
[[ "$image_id" =~ ^sha256:[0-9a-f]{64}$ ]] || fail "invalid immutable build image ID"

image_input_hash="$({
    for input in Containerfile.build .dockerignore global.json Directory.Build.props; do
        printf '%s\0' "$input"
        sha256sum "$REPO_DIR/$input" | cut -d' ' -f1
    done
} | sha256sum | cut -d' ' -f1)"
cache_input_hash="$(ghelper_cache_input_sha256 "$REPO_DIR")"
[[ "$cache_input_hash" =~ ^[0-9a-f]{64}$ ]] || fail "invalid cache-input hash"
host_cache_base="${XDG_CACHE_HOME:-}"
if [[ "$host_cache_base" != /* ]]; then
    [[ "${HOME:-}" == /* ]] || fail "absolute host cache root unavailable"
    host_cache_base="$HOME/.cache"
fi
host_build_cache="$host_cache_base/ghelper-x13-build/$cache_input_hash"
host_nuget="$host_build_cache/nuget"
[[ -d "$host_nuget" ]] || fail "canonical external NuGet cache is missing"
ghelper_nuget_validate_tree "$host_nuget" || fail "persistent NuGet cache has unsafe nodes"
host_cache_root="$host_cache_base/ghelper-x13-build"
host_cache_snapshot_before="$(tree_snapshot "$host_cache_root")"
mkdir -m 700 "$VERIFY_CACHE_HOME"
mkdir -m 700 "$VERIFY_CACHE_HOME/ghelper-x13-build"
mkdir -m 700 "$VERIFY_CACHE_HOME/ghelper-x13-build/$cache_input_hash"
# Only NuGet packages are build inputs.  Do not duplicate the persistent
# dotnet CLI/HTTP cache, and never let a verifier process write to either host
# cache tree.  build-container.sh will take its own read-only private snapshot
# from this disposable copy before compilation.
ghelper_nuget_materialize_closure "$REPO_DIR" "$host_nuget" \
    "$VERIFY_CACHE_HOME/ghelper-x13-build/$cache_input_hash/nuget"
mkdir -m 700 "$VERIFY_CACHE_HOME/ghelper-x13-build/$cache_input_hash/dotnet"

# From this point every command that may generate output runs only in a private
# copy. The reviewed checkout remains unmounted and byte-for-byte untouched.
materialize_source_snapshot "$REPO_DIR" "$VERIFY_SOURCE"
cd "$VERIFY_SOURCE"
if [[ "$review_mode" == "dirty-review" ]]; then
    build_env=(env XDG_CACHE_HOME="$VERIFY_CACHE_HOME" \
        GHELPER_DIRTY_REVIEW=1 GHELPER_OFFLINE=1)
else
    build_env=(env XDG_CACHE_HOME="$VERIFY_CACHE_HOME" GHELPER_OFFLINE=1)
fi

shared_nuget="$VERIFY_CACHE_HOME/ghelper-x13-build/$cache_input_hash/nuget"
[[ -d "$shared_nuget" ]] || fail "canonical external NuGet cache is missing"
container=(
    "$ENGINE" run --rm --network none
    "${container_user_args[@]}"
    --env HOME=/tmp
    --env DOTNET_CLI_HOME=/tmp/ghelper-test-dotnet
    --env NUGET_PACKAGES=/nuget
    --volume "$shared_nuget:/nuget:ro"
    --volume "$VERIFY_SOURCE:/work"
    --workdir /work
)

echo "== NuGet cache node-type rejection =="
for unsafe_type in symlink fifo; do
    unsafe_cache="$WORK_DIR/unsafe-cache-$unsafe_type"
    mkdir -m 700 "$unsafe_cache"
    case "$unsafe_type" in
        symlink) ln -s /etc/passwd "$unsafe_cache/escape" ;;
        fifo) mkfifo "$unsafe_cache/special.fifo" ;;
    esac
    set +e
    ghelper_nuget_validate_tree "$unsafe_cache" \
        >"$WORK_DIR/unsafe-cache-$unsafe_type.log" 2>&1
    unsafe_cache_rc=$?
    set -e
    [[ "$unsafe_cache_rc" != "0" ]] \
        || fail "NuGet cache accepted unsafe $unsafe_type node"
    find -P "$unsafe_cache" -depth -delete
done

echo "== Persistent cache path and no-follow lock rejection =="
cache_attack_root="$WORK_DIR/cache-path-attacks"
cache_attack_outside="$WORK_DIR/cache-path-outside"
mkdir -m 700 "$cache_attack_root" "$cache_attack_outside"
printf 'must remain unchanged\n' > "$cache_attack_outside/sentinel"
cache_attack_before="$(tree_snapshot "$cache_attack_outside")"

run_cache_path_rejection() {
    local name="$1" cache_home="$2" log="$WORK_DIR/cache-path-$1.log"
    set +e
    XDG_CACHE_HOME="$cache_home" GHELPER_DIRTY_REVIEW=1 GHELPER_OFFLINE=1 \
        ./build.sh --print-provenance >"$log" 2>&1
    local rc=$?
    set -e
    [[ "$rc" != "0" ]] || fail "unsafe persistent cache path was accepted: $name"
    grep -Eq 'persistent cache|cache\.lock|dotnet cache' "$log" \
        || fail "persistent cache refusal was not explicit: $name"
    [[ "$(tree_snapshot "$cache_attack_outside")" == "$cache_attack_before" ]] \
        || fail "unsafe cache path wrote outside its validated root: $name"
}

root_symlink="$cache_attack_root/root-symlink"
ln -s "$cache_attack_outside" "$root_symlink"
run_cache_path_rejection root-symlink "$root_symlink"

dotnet_root="$cache_attack_root/dotnet-root"
mkdir -m 700 "$dotnet_root" "$dotnet_root/ghelper-x13-build" \
    "$dotnet_root/ghelper-x13-build/$cache_input_hash" \
    "$dotnet_root/ghelper-x13-build/$cache_input_hash/nuget"
ln -s "$cache_attack_outside" \
    "$dotnet_root/ghelper-x13-build/$cache_input_hash/dotnet"
run_cache_path_rejection dotnet-symlink "$dotnet_root"
[[ ! -e "$dotnet_root/ghelper-x13-build/$cache_input_hash/cache.lock" ]] \
    || fail "cache lock was opened after unsafe dotnet path detection"

lock_root="$cache_attack_root/lock-root"
mkdir -m 700 "$lock_root" "$lock_root/ghelper-x13-build" \
    "$lock_root/ghelper-x13-build/$cache_input_hash" \
    "$lock_root/ghelper-x13-build/$cache_input_hash/nuget" \
    "$lock_root/ghelper-x13-build/$cache_input_hash/dotnet"
ln -s "$cache_attack_outside/sentinel" \
    "$lock_root/ghelper-x13-build/$cache_input_hash/cache.lock"
run_cache_path_rejection lock-symlink "$lock_root"
[[ "$(cat "$cache_attack_outside/sentinel")" == 'must remain unchanged' ]] \
    || fail "cache.lock symlink target was modified"

if [[ "$review_mode" == "dirty-review" ]]; then
    set +e
    XDG_CACHE_HOME="$VERIFY_CACHE_HOME" GHELPER_DIRTY_REVIEW=0 GHELPER_OFFLINE=1 \
        ./scripts/build-container.sh --print-provenance \
        >"$WORK_DIR/default-dirty.log" 2>&1
    default_dirty_rc=$?
    set -e
    [[ "$default_dirty_rc" == "1" ]] \
        || fail "default build did not reject an uncommitted tree"
    grep -Fq 'dirty tree refused' "$WORK_DIR/default-dirty.log" \
        || fail "default dirty-tree refusal is not explicit"
fi

echo "== Destructive-output refusal and ownership checks =="
unmarked_output="$WORK_DIR/unmarked-output"
symlink_target="$WORK_DIR/symlink-target"
symlink_output="$WORK_DIR/symlink-output"
mkdir -p "$unmarked_output" "$symlink_target"
ln -s "$symlink_target" "$symlink_output"
output_rejections=(
    /tmp
    "${HOME:-/nonexistent}"
    "$REPO_DIR"
    "$(dirname "$REPO_DIR")"
    "$(dirname "$(dirname "$REPO_DIR")")"
    "$unmarked_output"
    "$symlink_output"
)
for unsafe_output in "${output_rejections[@]}"; do
    rejection_key="$(printf '%s' "$unsafe_output" | sha256sum | cut -c1-12)"
    set +e
    "${build_env[@]}" ./build.sh --no-aot --output "$unsafe_output" \
        >"$WORK_DIR/output-reject-$rejection_key.log" 2>&1
    rejection_rc=$?
    set -e
    [[ "$rejection_rc" == "2" ]] \
        || fail "unsafe output was not rejected with EX_USAGE: $unsafe_output"
    grep -Eq 'refusing unsafe build output path|not a builder-owned directory' \
        "$WORK_DIR/output-reject-$rejection_key.log" \
        || fail "unsafe output refusal was not explicit: $unsafe_output"
    ! grep -Fq 'Staged build snapshot:' "$WORK_DIR/output-reject-$rejection_key.log" \
        || fail "unsafe output was rejected only after build staging: $unsafe_output"
done

echo "== Internal wrapper-context spoof rejection =="
fake_context_dir="$WORK_DIR/fake-context-dir"
fake_context="$WORK_DIR/fake-context"
fake_token="$WORK_DIR/fake-token"
fake_findmnt_dir="$WORK_DIR/fake-findmnt"
mkdir -p "$fake_context_dir" "$fake_findmnt_dir"
printf 'format=ghelper-phase1-build-context-v1\nwrapper_nonce=%064d\n' 0 \
    > "$fake_context"
printf 'ghelper-phase1-wrapper-v1:%064d\n' 1 > "$fake_token"
cp "$fake_context" "$fake_context_dir/ghelper-build-context"
cp "$fake_token" "$fake_context_dir/ghelper-build-token"
chmod 400 "$fake_context" "$fake_token" \
    "$fake_context_dir/ghelper-build-context" "$fake_context_dir/ghelper-build-token"
cat > "$fake_findmnt_dir/findmnt" <<'EOF'
#!/bin/sh
path=""
while [ "$#" -gt 0 ]; do
    if [ "$1" = "-M" ]; then path="$2"; shift 2; else shift; fi
done
printf '%s errors=remount-ro\n' "$path"
EOF
chmod 700 "$fake_findmnt_dir/findmnt"

context_spoof_case() {
    local name="$1"
    shift
    set +e
    "$ENGINE" run --rm --network none "${container_user_args[@]}" \
        --env HOME=/tmp --volume "$VERIFY_SOURCE:/work:ro" \
        --workdir /work "$@" "$image_id" ./build.sh --print-provenance \
        >"$WORK_DIR/context-$name.log" 2>&1
    local rc=$?
    set -e
    [[ "$rc" == "1" ]] || fail "wrapper context spoof case did not fail: $name"
    grep -Eq 'wrapper-owned read-only context|handshake token' \
        "$WORK_DIR/context-$name.log" \
        || fail "wrapper context spoof refusal was not explicit: $name"
}

context_spoof_case parent-mount \
    --volume "$fake_context_dir:/tmp:ro"
context_spoof_case writable-context \
    --volume "$fake_context:/tmp/ghelper-build-context:rw" \
    --volume "$fake_token:/tmp/ghelper-build-token:ro"
context_spoof_case nonce-mismatch \
    --volume "$fake_context:/tmp/ghelper-build-context:ro" \
    --volume "$fake_token:/tmp/ghelper-build-token:ro"
context_spoof_case remount-ro-substring \
    --env PATH=/fakebin:/usr/bin:/bin \
    --volume "$fake_findmnt_dir:/fakebin:ro" \
    --volume "$fake_context:/tmp/ghelper-build-context:ro" \
    --volume "$fake_token:/tmp/ghelper-build-token:ro"

echo "== Coherent wrapper handshake is explicitly not authentication =="
coherent_context="$WORK_DIR/coherent-build-context"
coherent_token="$WORK_DIR/coherent-build-token"
coherent_nonce="$(printf 'phase1-coherent-guard-test' | sha256sum | cut -d' ' -f1)"
coherent_nuget_manifest="$WORK_DIR/coherent-nuget.manifest"
ghelper_nuget_manifest "$shared_nuget" > "$coherent_nuget_manifest"
coherent_nuget_hash="$(sha256sum "$coherent_nuget_manifest" | cut -d' ' -f1)"
coherent_environment_hash="$({
    printf 'format=ghelper-phase1-environment-v2\n'
    printf 'image_id=%s\n' "$image_id"
    printf 'image_input_sha256=%s\n' "$image_input_hash"
    printf 'cache_input_sha256=%s\n' "$cache_input_hash"
    printf 'nuget_manifest_sha256=%s\n' "$coherent_nuget_hash"
} | sha256sum | cut -d' ' -f1)"
if [[ "$review_mode" == "dirty-review" ]]; then
    GHELPER_DIRTY_REVIEW=1 ghelper_set_provenance "$VERIFY_SOURCE" "$coherent_environment_hash"
else
    GHELPER_DIRTY_REVIEW=0 ghelper_set_provenance "$VERIFY_SOURCE" "$coherent_environment_hash"
fi
coherent_version="$GHELPER_INFORMATIONAL_VERSION"
{
    printf 'format=ghelper-phase1-build-context-v1\n'
    printf 'wrapper_nonce=%s\n' "$coherent_nonce"
    printf 'source_provenance=%s\n' "$GHELPER_BUILD_PROVENANCE"
    printf 'build_mode=%s\n' "$GHELPER_BUILD_MODE"
    printf 'informational_version=%s\n' "$coherent_version"
    printf 'image_id=%s\n' "$image_id"
    printf 'image_input_sha256=%s\n' "$image_input_hash"
    printf 'cache_input_sha256=%s\n' "$cache_input_hash"
    printf 'nuget_manifest_sha256=%s\n' "$coherent_nuget_hash"
    printf 'environment_sha256=%s\n' "$coherent_environment_hash"
    printf 'nuget_manifest_begin\n'
    cat "$coherent_nuget_manifest"
    printf 'nuget_manifest_end\n'
} > "$coherent_context"
printf 'ghelper-phase1-wrapper-v1:%s\n' "$coherent_nonce" > "$coherent_token"
chmod 400 "$coherent_context" "$coherent_token"
coherent_guard_output="$($ENGINE run --rm --network none \
    "${container_user_args[@]}" --env HOME=/tmp --env NUGET_PACKAGES=/nuget \
    --volume "$VERIFY_SOURCE:/work" --volume "$shared_nuget:/nuget:ro" \
    --volume "$coherent_context:/tmp/ghelper-build-context:ro" \
    --volume "$coherent_token:/tmp/ghelper-build-token:ro" \
    --workdir /work "$image_id" ./build.sh --print-provenance | tail -n 1)"
[[ "$coherent_guard_output" == "$coherent_version" ]] \
    || fail "coherent accidental-misuse handshake did not describe local provenance"
rg -q 'accidental-misuse guard, not authentication' README.md docs/reproducible-build.md \
    || fail "documentation overstates wrapper handshake trust"
unset GHELPER_BUILD_MODE GHELPER_DIFF_HASH GHELPER_BUILD_PROVENANCE \
    GHELPER_INFORMATIONAL_VERSION GHELPER_BUILD_ENV_HASH

echo "== Caller provenance spoof rejection =="
set +e
GHELPER_BUILD_PROVENANCE=spoofed-by-caller \
GHELPER_INFORMATIONAL_VERSION=spoofed-version \
    "${build_env[@]}" ./build.sh --print-provenance \
    >"$WORK_DIR/provenance-spoof.out" 2>"$WORK_DIR/provenance-spoof.err"
spoof_rc=$?
set -e
[[ "$spoof_rc" == "0" ]] || fail "internal provenance recomputation failed"
expected_version="$(tail -n 1 "$WORK_DIR/provenance-spoof.out")"
[[ "$expected_version" =~ ^1\.0\.90-x13\.1\+([0-9a-f]{40}|dirty\.[0-9a-f]{64}\.base\.[0-9a-f]{40})\.env\.[0-9a-f]{64}$ ]] \
    || fail "computed local build provenance is malformed"
expected_metadata=$'LOCAL-REPRODUCIBLE-UNSIGNED\t'"$expected_version"
! grep -Fq 'spoofed-' "$WORK_DIR/provenance-spoof.out" "$WORK_DIR/provenance-spoof.err" \
    || fail "caller-controlled provenance leaked into build output"
grep -Fq 'Ignoring caller-supplied provenance metadata' "$WORK_DIR/provenance-spoof.err" \
    || fail "caller provenance override was not explicitly rejected"

echo "== Clean CI provenance simulation (no dirty flag) =="
clean_repo="$WORK_DIR/clean-repo"
materialize_source_snapshot "$VERIFY_SOURCE" "$clean_repo"
git -C "$clean_repo" add -A
if ! git -C "$clean_repo" diff --cached --quiet; then
    git -C "$clean_repo" -c user.name='Phase 1 verifier' \
        -c user.email='verifier.invalid' commit --quiet \
        -m 'ephemeral clean verification snapshot'
fi
[[ -z "$(git -C "$clean_repo" status --porcelain=v1 --untracked-files=all)" ]] \
    || fail "clean provenance snapshot is not actually clean"
clean_version="$(cd "$clean_repo" && XDG_CACHE_HOME="$VERIFY_CACHE_HOME" GHELPER_OFFLINE=1 \
    ./build.sh --print-provenance | tail -n 1)"
[[ "$clean_version" =~ ^1\.0\.90-x13\.1\+[0-9a-f]{40}\.env\.[0-9a-f]{64}$ ]] \
    || fail "clean snapshot did not build provenance without dirty-review permission"

echo "== Simulated GitHub runner preparation =="
runner_temp="$WORK_DIR/runner-temp"
mkdir -m 700 "$runner_temp"
runner_cache="$runner_temp/ghelper-phase1-cache"
mkdir -m 700 "$runner_cache" "$runner_cache/ghelper-x13-build" \
    "$runner_cache/ghelper-x13-build/$cache_input_hash"
ghelper_nuget_materialize_closure "$REPO_DIR" "$host_nuget" \
    "$runner_cache/ghelper-x13-build/$cache_input_hash/nuget"
mkdir -m 700 "$runner_cache/ghelper-x13-build/$cache_input_hash/dotnet"
(cd "$VERIFY_SOURCE" && RUNNER_TEMP="$runner_temp" "${build_env[@]}" \
    ./scripts/prepare-ci.sh) >"$WORK_DIR/prepare-ci.log" 2>&1
[[ "$(stat -c %a "$runner_temp")" == "700" \
   && -d "$runner_temp/ghelper-phase1-cache" \
   && "$(stat -c %a "$runner_temp/ghelper-phase1-cache")" == "700" \
   && -z "$(find "$runner_temp" -mindepth 1 -maxdepth 1 \
       ! -name ghelper-phase1-cache -print -quit)" ]] \
    || fail "CI preparation did not use and clean a private mode-0700 RUNNER_TEMP child"
[[ -f "$runner_cache/ghelper-x13-build/$cache_input_hash/cache.lock" \
   && ! -L "$runner_cache/ghelper-x13-build/$cache_input_hash/cache.lock" \
   && "$(stat -c %a "$runner_cache/ghelper-x13-build/$cache_input_hash/cache.lock")" == "600" ]] \
    || fail "simulated CI did not create a safe regular cache lock"
[[ "$runner_cache" == "$WORK_DIR/runner-temp/ghelper-phase1-cache" \
   && -d "$runner_cache" && ! -L "$runner_cache" ]] \
    || fail "unsafe simulated-runner cache cleanup path"
find -P "$runner_cache" -depth -delete

echo "== Isolated snapshot mutation regression =="
mutation_repo="$WORK_DIR/mutation-repo"
materialize_source_snapshot "$VERIFY_SOURCE" "$mutation_repo"
snapshot_probe="$mutation_repo/src/Phase1SnapshotProbe.cs"
printf '%s\n' \
    'namespace GHelper.Linux;' \
    'internal static class Phase1SnapshotProbe { internal const int Value = 1; }' \
    > "$snapshot_probe"
snapshot_log="$WORK_DIR/snapshot-mutation.log"
(cd "$mutation_repo" && XDG_CACHE_HOME="$VERIFY_CACHE_HOME" \
    GHELPER_DIRTY_REVIEW=1 GHELPER_OFFLINE=1 \
    ./build.sh --no-aot --output "$WORK_DIR/mutation-output") \
    >"$snapshot_log" 2>&1 &
snapshot_pid=$!
snapshot_ready=0
for _ in $(seq 1 1200); do
    if grep -Fq 'Staged build snapshot:' "$snapshot_log"; then
        snapshot_ready=1
        break
    fi
    if ! kill -0 "$snapshot_pid" 2>/dev/null; then
        break
    fi
    sleep 0.1
done
[[ "$snapshot_ready" == "1" ]] || {
    wait "$snapshot_pid" || true
    sed -n '1,200p' "$snapshot_log" >&2
    fail "isolated build did not expose its staged snapshot"
}
staged_probe_version="$(sed -n 's/^Staged build snapshot: //p' "$snapshot_log" | head -n 1)"
[[ "$staged_probe_version" =~ ^1\.0\.90-x13\.1\+dirty\.[0-9a-f]{64}\.base\.[0-9a-f]{40}\.env\.[0-9a-f]{64}$ ]] \
    || fail "staged mutation snapshot lacks exact dirty/environment provenance"
# Deliberately corrupt the shared checkout after the snapshot/provenance stamp.
# Compilation must continue solely from the private source tree.
printf '%s\n' 'this is intentionally invalid C# after snapshot staging' > "$snapshot_probe"
if ! wait "$snapshot_pid"; then
    sed -n '1,240p' "$snapshot_log" >&2
    fail "shared-worktree mutation affected isolated compilation"
fi
probe_artifact_version="$($ENGINE run --rm --network none \
    "${container_user_args[@]}" --env HOME=/tmp \
    --volume "$WORK_DIR/mutation-output:/artifact:ro" \
    "$image_id" /artifact/ghelper --print-build-metadata)"
[[ "$probe_artifact_version" == $'LOCAL-REPRODUCIBLE-UNSIGNED\t'"$staged_probe_version" ]] \
    || fail "snapshot artifact metadata does not describe compiled snapshot inputs"
rm -f "$snapshot_probe"

owned_marker="$WORK_DIR/mutation-output/.ghelper-x13-output-owner-v1"
[[ -f "$owned_marker" && ! -L "$owned_marker" \
   && "$(stat -c %a "$owned_marker")" == "600" ]] \
    || fail "new build output lacks its protected ownership marker"
printf 'must disappear on atomic replacement\n' \
    > "$WORK_DIR/mutation-output/replacement-sentinel"
replacement_log="$WORK_DIR/output-replacement.log"
set +e
(cd "$mutation_repo" && XDG_CACHE_HOME="$VERIFY_CACHE_HOME" \
    GHELPER_DIRTY_REVIEW=1 GHELPER_OFFLINE=1 \
    ./build.sh --no-aot --output "$WORK_DIR/mutation-output") \
    >"$replacement_log" 2>&1
replacement_rc=$?
set -e
if [[ "$replacement_rc" != "0" ]]; then
    sed -n '1,240p' "$replacement_log" >&2
    fail "builder-owned output replacement failed"
fi
[[ ! -e "$WORK_DIR/mutation-output/replacement-sentinel" \
   && -f "$WORK_DIR/mutation-output/.ghelper-x13-output-owner-v1" ]] \
    || fail "builder-owned output was not atomically and safely replaced"
replacement_staged_version="$(sed -n 's/^Staged build snapshot: //p' \
    "$replacement_log" | head -n 1)"
replacement_artifact_version="$($ENGINE run --rm --network none \
    "${container_user_args[@]}" --env HOME=/tmp \
    --volume "$WORK_DIR/mutation-output:/artifact:ro" \
    "$image_id" /artifact/ghelper --print-build-metadata)"
[[ "$replacement_artifact_version" == $'LOCAL-REPRODUCIBLE-UNSIGNED\t'"$replacement_staged_version" ]] \
    || fail "atomic output replacement did not publish the staged artifact"

echo "== Shared NuGet cache mutation isolation regression =="
mutation_cache_home="$MUTATION_CACHE_HOME"
mutation_cache_root="$mutation_cache_home/ghelper-x13-build/$cache_input_hash"
mutation_shared_nuget="$mutation_cache_root/nuget"
mkdir -m 700 "$mutation_cache_home"
mkdir -m 700 "$mutation_cache_home/ghelper-x13-build"
mkdir -m 700 "$mutation_cache_root"
ghelper_nuget_materialize_closure "$REPO_DIR" "$host_nuget" "$mutation_shared_nuget"
mkdir -m 700 "$mutation_cache_root/dotnet"
# A syntactically safe but unlocked extra package must be ignored when the
# wrapper rematerializes the exact closure into its private cache.
mkdir -p "$mutation_shared_nuget/unlocked.contaminant/9.9.9"
printf 'not in any lock file\n' \
    > "$mutation_shared_nuget/unlocked.contaminant/9.9.9/payload"
cache_probe="$(find "$mutation_shared_nuget" -type f -size +0c -print -quit)"
[[ -n "$cache_probe" ]] || fail "no shared NuGet cache file available for mutation test"
cache_original_hash="$(sha256sum "$cache_probe" | cut -d' ' -f1)"
cache_log="$WORK_DIR/cache-mutation.log"
(cd "$mutation_repo" && XDG_CACHE_HOME="$mutation_cache_home" \
    GHELPER_DIRTY_REVIEW=1 GHELPER_OFFLINE=1 \
    ./build.sh --no-aot --output "$WORK_DIR/cache-mutation-output") \
    >"$cache_log" 2>&1 &
cache_pid=$!
cache_ready=0
for _ in $(seq 1 1200); do
    if grep -Fq 'Staged build snapshot:' "$cache_log"; then cache_ready=1; break; fi
    kill -0 "$cache_pid" 2>/dev/null || break
    sleep 0.1
done
[[ "$cache_ready" == "1" ]] || {
    wait "$cache_pid" || true
    sed -n '1,240p' "$cache_log" >&2
    fail "cache-isolation build never staged"
}
printf 'tampered shared cache after private snapshot\n' > "$cache_probe"
if ! wait "$cache_pid"; then
    fail "shared-cache mutation affected private-cache compilation"
fi
cache_staged_version="$(sed -n 's/^Staged build snapshot: //p' "$cache_log" | head -n 1)"
cache_artifact_version="$($ENGINE run --rm --network none \
    "${container_user_args[@]}" --env HOME=/tmp \
    --volume "$WORK_DIR/cache-mutation-output:/artifact:ro" \
    "$image_id" /artifact/ghelper --print-build-metadata)"
[[ "$cache_artifact_version" == $'LOCAL-REPRODUCIBLE-UNSIGNED\t'"$cache_staged_version" ]] \
    || fail "shared-cache mutation changed artifact provenance or inputs"
[[ "$(sha256sum "$cache_probe" | cut -d' ' -f1)" != "$cache_original_hash" ]] \
    || fail "private cache mutation probe did not alter its disposable copy"
cmp -s "$WORK_DIR/mutation-output/.ghelper-build-manifest-v1" \
       "$WORK_DIR/cache-mutation-output/.ghelper-build-manifest-v1" \
    || fail "fresh versus contaminated cache changed environment/artifact manifest"
[[ "$(sha256sum "$WORK_DIR/mutation-output/ghelper" | cut -d' ' -f1)" == \
   "$(sha256sum "$WORK_DIR/cache-mutation-output/ghelper" | cut -d' ' -f1)" ]] \
    || fail "fresh versus contaminated cache changed artifact bytes"

# Reclaim only verifier-owned paths before the two AOT builds.  Keeping these
# large disposable snapshots alive would make the acceptance depend on tmpfs
# capacity rather than on the reviewed build.
for disposable_path in \
    "$MUTATION_CACHE_HOME" \
    "$WORK_DIR/cache-mutation-output" \
    "$WORK_DIR/mutation-output"; do
    [[ "$disposable_path" == "$WORK_DIR/"* \
       && "$disposable_path" != "$WORK_DIR" \
       && ! -L "$disposable_path" ]] \
        || fail "unsafe verifier disposable path: $disposable_path"
    if [[ -e "$disposable_path" ]]; then
        find -P "$disposable_path" -depth -delete
    fi
done

echo "== Native AOT build 1/2 (offline, locked) =="
"${build_env[@]}" ./build.sh --output "$WORK_DIR/aot-1" 2>&1 | tee "$WORK_DIR/aot-1.log"
! grep -Fq IL2008 "$WORK_DIR/aot-1.log" || fail "IL2008 present in AOT build 1"
[[ -x "$WORK_DIR/aot-1/ghelper" ]] || fail "Native AOT artifact missing after build 1"
hash_one="$(sha256sum "$WORK_DIR/aot-1/ghelper" | cut -d' ' -f1)"
assert_no_generated_vendor_outputs

echo "== Native AOT build 2/2 (offline, locked) =="
"${build_env[@]}" ./build.sh --output "$WORK_DIR/aot-2" 2>&1 | tee "$WORK_DIR/aot-2.log"
! grep -Fq IL2008 "$WORK_DIR/aot-2.log" || fail "IL2008 present in AOT build 2"
hash_two="$(sha256sum "$WORK_DIR/aot-2/ghelper" | cut -d' ' -f1)"
[[ "$hash_one" == "$hash_two" ]] || fail "Native AOT output is not deterministic"
[[ "$(find "$WORK_DIR/aot-2" -maxdepth 1 -type f | wc -l)" == "3" \
   && -f "$WORK_DIR/aot-2/.ghelper-x13-output-owner-v1" \
   && -f "$WORK_DIR/aot-2/.ghelper-build-manifest-v1" ]] \
    || fail "dist does not contain AOT artifact plus external manifest/ownership marker"
cmp -s "$WORK_DIR/aot-1/.ghelper-build-manifest-v1" \
       "$WORK_DIR/aot-2/.ghelper-build-manifest-v1" \
    || fail "external reproducibility manifest is not deterministic"
artifact="$WORK_DIR/aot-2/ghelper"
metadata_home="$WORK_DIR/metadata-home"
metadata_config="$WORK_DIR/metadata-config"
metadata_cache="$WORK_DIR/metadata-cache"
metadata_data="$WORK_DIR/metadata-data"
metadata_cwd="$WORK_DIR/metadata-cwd"
mkdir -p "$metadata_home" "$metadata_config" "$metadata_cache" \
    "$metadata_data" "$metadata_cwd"
metadata_before="$(tree_snapshot "$WORK_DIR")"
artifact_version="$(cd "$metadata_cwd" && HOME="$metadata_home" \
    XDG_CONFIG_HOME="$metadata_config" XDG_CACHE_HOME="$metadata_cache" \
    XDG_DATA_HOME="$metadata_data" LC_ALL=C LANG=C \
    "$artifact" --print-build-metadata)"
[[ "$artifact_version" == "$expected_metadata" ]] \
    || fail "AOT assembly informational version mismatch: $artifact_version"
metadata_after="$(tree_snapshot "$WORK_DIR")"
[[ "$metadata_before" == "$metadata_after" ]] \
    || fail "metadata inspection initialized persistent application state"
assert_no_generated_vendor_outputs

echo "== External unsigned manifest and rebuild verification =="
forged_artifact="$WORK_DIR/forged-artifact"
forged_sentinel="$WORK_DIR/untrusted-candidate-was-executed"
cp -a "$WORK_DIR/aot-2" "$forged_artifact"
rm -f "$forged_artifact/ghelper" "$forged_artifact/.ghelper-build-manifest-v1"
printf '#!/usr/bin/env bash\nprintf executed > %q\nexit 0\n' \
    "$forged_sentinel" > "$forged_artifact/ghelper"
chmod 700 "$forged_artifact/ghelper"
source scripts/artifact-manifest.sh
nuget_manifest_hash="$(ghelper_nuget_manifest_sha256 "$shared_nuget")"
environment_hash="${expected_version##*.env.}"
ghelper_write_artifact_manifest \
    "$forged_artifact" "$expected_version" "$expected_provenance" "$review_mode" \
    "$image_id" "$image_input_hash" "$cache_input_hash" \
    "$nuget_manifest_hash" "$environment_hash"
"${build_env[@]}" ./scripts/verify-artifact.sh \
    --also-reject "$forged_artifact" "$WORK_DIR/aot-2" \
    | tee "$WORK_DIR/verify-artifact.log"
grep -Fq 'Rejected internally coherent unsigned forgery by independent rebuild.' \
    "$WORK_DIR/verify-artifact.log" \
    || fail "canonical rebuild verifier did not reject coherent unsigned forgery"
[[ ! -e "$forged_sentinel" ]] \
    || fail "canonical verifier executed an untrusted candidate"
! rg -q '^[[:space:]]*"\$(candidate|REJECT_DIR)/ghelper"' scripts/verify-artifact.sh \
    || fail "canonical verifier contains an untrusted candidate execution path"
rg -q '"\$rebuild_output/ghelper" --print-build-metadata' scripts/verify-artifact.sh \
    || fail "canonical verifier does not defer execution to the rebuilt binary"

assembly="$shared_nuget/avalonia.freedesktop/12.1.1/lib/net10.0/Avalonia.FreeDesktop.dll"
[[ -f "$assembly" ]] || fail "locked Avalonia metadata missing after restore"
grep -aFq 'Avalonia.FreeDesktop.DBus.IStatusNotifierItemHandler' "$assembly" \
    || fail "current StatusNotifierItem handler contract not found"
! rg -q 'Tmds\.DBus\.SourceGenerator\.OrgKdeStatusNotifierItemHandler' src/TrimmerRoots.xml \
    || fail "stale trimmer root remains"

english_refusal="$(awk -F ' = ' '/\["udev_not_installed"\]/{gsub(/^"|",?$/, "", $2); print $2; exit}' \
    src/I18n/Languages/English.cs)"
for config_mode in unset empty relative absolute all-invalid; do
    case_dir="$WORK_DIR/cli-$config_mode"
    mkdir -p "$case_dir/home" "$case_dir/config" "$case_dir/cache" \
        "$case_dir/data" "$case_dir/cwd"
    printf 'snapshot\n' > "$case_dir/home/marker"
    printf 'snapshot\n' > "$case_dir/config/marker"
    printf 'snapshot\n' > "$case_dir/cache/marker"
    printf 'snapshot\n' > "$case_dir/data/marker"
    cli_before="$(tree_snapshot "$case_dir")"

    for invocation in plain metadata-extra metadata-prefixed extract apply remove install; do
        case "$invocation" in
            plain)               args=() ;;
            metadata-extra)      args=(--print-build-metadata unexpected) ;;
            metadata-prefixed)   args=(unexpected --print-build-metadata) ;;
            extract)             args=(--extract-helper gpu-helper "$case_dir/extracted-helper") ;;
            apply)               args=(--apply-system-files all) ;;
            remove)              args=(--remove-system-files all) ;;
            install)             args=(--install-gpu-helper "$case_dir/helper-dir") ;;
        esac
        output="$WORK_DIR/cli-$config_mode-$invocation.out"
        set +e
        cli_container=(
            "$ENGINE" run --rm --network none
            "${container_user_args[@]}"
            --env HOME="$case_dir/home"
            --env LC_ALL=C
            --env LANG=C
            --volume "$WORK_DIR:$WORK_DIR"
            --workdir "$case_dir/cwd"
        )
        case "$config_mode" in
            unset)
                "${cli_container[@]}" "$image_id" /usr/bin/env \
                    -u XDG_CONFIG_HOME -u XDG_CACHE_HOME -u XDG_DATA_HOME \
                    "$artifact" "${args[@]}" >"$output" 2>&1
                ;;
            empty)
                "${cli_container[@]}" --env XDG_CONFIG_HOME= \
                    --env XDG_CACHE_HOME= --env XDG_DATA_HOME= \
                    "$image_id" "$artifact" "${args[@]}" >"$output" 2>&1
                ;;
            relative)
                "${cli_container[@]}" --env XDG_CONFIG_HOME=relative-config \
                    --env XDG_CACHE_HOME=relative-cache --env XDG_DATA_HOME=relative-data \
                    "$image_id" "$artifact" "${args[@]}" >"$output" 2>&1
                ;;
            absolute)
                "${cli_container[@]}" --env XDG_CONFIG_HOME="$case_dir/config" \
                    --env XDG_CACHE_HOME="$case_dir/cache" \
                    --env XDG_DATA_HOME="$case_dir/data" \
                    "$image_id" "$artifact" "${args[@]}" >"$output" 2>&1
                ;;
            all-invalid)
                "${cli_container[@]}" --env HOME=relative-home \
                    --env XDG_CONFIG_HOME=relative-config \
                    --env XDG_CACHE_HOME=relative-cache --env XDG_DATA_HOME=relative-data \
                    "$image_id" "$artifact" "${args[@]}" >"$output" 2>&1
                ;;
        esac
        cli_rc=$?
        set -e
        [[ "$cli_rc" == "64" ]] \
            || fail "$invocation did not fail closed with $config_mode user paths"
        grep -Fq "$english_refusal" "$output" \
            || fail "$invocation refusal is not explicit with $config_mode user paths"
    done

    cli_after="$(tree_snapshot "$case_dir")"
    [[ "$cli_before" == "$cli_after" ]] \
        || fail "blocked runtime/CLI mutated HOME, XDG, or cwd with $config_mode user paths"
done

echo "== Locked test restores and C# scenarios =="
"${container[@]}" "$image_id" dotnet restore \
    src/GHelper.Linux.csproj --runtime linux-x64 --locked-mode
set +e
"${container[@]}" "$image_id" dotnet publish src/GHelper.Linux.csproj \
    -c Release --no-restore --no-dependencies \
    >"$WORK_DIR/direct-release.log" 2>&1
direct_release_rc=$?
set -e
[[ "$direct_release_rc" != "0" ]] \
    || fail "direct UNATTESTED dotnet Release publish unexpectedly succeeded"
grep -Fq 'UNATTESTED Release publishing is disabled outside the canonical local reproducible-build wrapper' \
    "$WORK_DIR/direct-release.log" \
    || fail "direct Release publish refusal is not explicit"

# The MSBuild inputs are intentionally not a secret. A caller can coherently
# forge them and obtain a binary, but without wrapper-produced context its
# metadata fails closed and canonical external verification refuses it.
forged_direct="$VERIFY_SOURCE/forged-direct"
"${container[@]}" "$image_id" dotnet publish src/GHelper.Linux.csproj \
    -c Release --no-restore -r linux-x64 --self-contained true \
    -p:PublishAot=false -p:PublishTrimmed=false -p:StripSymbols=false \
    -p:PublishDir=/work/forged-direct/ \
    -p:GHelperCanonicalLocalBuild=true \
    -p:InformationalVersion="1.0.90-x13.1+$(printf 'a%.0s' {1..40}).env.$(printf 'e%.0s' {1..64})" \
    -p:SourceRevisionId="$(printf 'a%.0s' {1..40})" -p:GHelperBuildMode=clean \
    -p:GHelperBuildImageId="sha256:$(printf 'b%.0s' {1..64})" \
    -p:GHelperImageInputSha256="$(printf 'c%.0s' {1..64})" \
    -p:GHelperCacheInputSha256="$(printf 'd%.0s' {1..64})" \
    -p:GHelperNuGetManifestSha256="$(printf 'f%.0s' {1..64})" \
    -p:GHelperBuildEnvironmentSha256="$(printf 'e%.0s' {1..64})" \
    >"$WORK_DIR/direct-forged-release.log" 2>&1
set +e
"$forged_direct/ghelper" --print-build-metadata \
    >"$WORK_DIR/direct-forged-metadata.out" 2>&1
forged_metadata_rc=$?
GHELPER_DIRTY_REVIEW=1 GHELPER_OFFLINE=1 \
    ./scripts/verify-artifact.sh "$forged_direct" \
    >"$WORK_DIR/direct-forged-verify.log" 2>&1
forged_verify_rc=$?
set -e
[[ "$forged_metadata_rc" == "70" && "$forged_verify_rc" != "0" ]] \
    || fail "coherently forged MSBuild properties produced canonically trusted metadata/artifact"
grep -Fq 'unsigned external build manifest missing or unsafe' \
    "$WORK_DIR/direct-forged-verify.log" \
    || fail "canonical verifier did not explicitly refuse direct forged publish"
[[ "$forged_direct" == "$VERIFY_SOURCE/forged-direct" \
   && -d "$forged_direct" && ! -L "$forged_direct" ]] || fail "unsafe forged-output cleanup"
find -P "$forged_direct" -depth -delete
"${container[@]}" "$image_id" dotnet restore \
    tests/GHelper.Linux.Tests/GHelper.Linux.Tests.csproj --locked-mode
"${container[@]}" "$image_id" dotnet restore \
    daemon/GHelper.Daemon.csproj --runtime linux-x64 --locked-mode
set +e
"${container[@]}" "$image_id" dotnet publish daemon/GHelper.Daemon.csproj \
    -c Release --no-restore >"$WORK_DIR/direct-daemon-release.log" 2>&1
direct_daemon_release_rc=$?
set -e
[[ "$direct_daemon_release_rc" != "0" ]] \
    || fail "direct UNATTESTED ghelperd Release publish unexpectedly succeeded"
grep -Fq 'UNATTESTED ghelperd Release publishing is disabled' \
    "$WORK_DIR/direct-daemon-release.log" \
    || fail "direct ghelperd Release refusal is not explicit"
"${container[@]}" "$image_id" dotnet build daemon/GHelper.Daemon.csproj \
    -c Debug --no-restore
"${container[@]}" "$image_id" dotnet restore \
    audio-helper/tests/cs/TestAudioPipeline.csproj --locked-mode
"${container[@]}" "$image_id" dotnet run \
    --project tests/GHelper.Linux.Tests/GHelper.Linux.Tests.csproj \
    -c Debug --no-restore 2>&1 | tee "$WORK_DIR/csharp.log"
grep -Fq 'Total:  149' "$WORK_DIR/csharp.log" || fail "C# scenario count changed"
grep -Fq 'Passed: 149' "$WORK_DIR/csharp.log" || fail "C# scenarios failed"
grep -Fq 'Failed: 0' "$WORK_DIR/csharp.log" || fail "C# scenarios failed"
for security_test in \
    AtomicPayload_StaleCacheIsReplaced \
    AtomicPayload_InterruptedWriteIsRejected \
    AtomicPayload_ConcurrentVersionsRemainIsolated \
    ExternalPrivilegedHelpers_AreUnavailable \
    NixHelperDiscovery_IsDisabled \
    RyzenAdjFeature_IsDisabled \
    UserPaths_EmptyXdgUsesAbsoluteHomeFallback \
    UserPaths_RelativeXdgUsesAbsoluteProfileFallback \
    UserPaths_AbsoluteXdgValuesAreHonored \
    UserPaths_AllInvalidFailsClosed \
    AtomicPayload_RelativeRootsAreRejected \
    UserPaths_TraversalAndSeparatorsAreRejected \
    MetadataCli_UnstampedAssemblyReturns70 \
    MetadataCli_MalformedArityReturns64 \
    MetadataCli_IncoherentLocalProvenanceIsRejected \
    Contract_IsVersionedAndIntrospectable \
    Contract_HasNoSpoofableCallerParameters \
    Capabilities_AdvertiseOnlyImplementedMutationAndAreDefensive \
    UnknownMutation_FailsBeforeIdentityLookup \
    KnownMutations_FailClosedBeforeBusCalls \
    GranularAuthorization_UsesMappedActionAndResolvedCaller \
    AuthorizedMutation_StillHasNoExecutor \
    AuthorizedXgMutation_IsQueuedAfterPolkit \
    AuthorizedDgpuMutation_IsQueuedAfterPolkit \
    InvalidSender_FailsBeforeIdentityLookupWhenEnabled \
    FutureMutationPipeline_IsBounded \
    BoundedTransport_RetainsSlotUntilCancelledCallCompletes \
    Resolver_ExercisesRealValidationAndTransport \
    Resolver_TimeoutLeavesUnderlyingCallBounded \
    Polkit_CancelsWithUniqueIdsOnCancelAndTimeout \
    Polkit_SaturationFailsClosed \
    Polkit_CancelFailureAbortsDedicatedTransport \
    Handler_UsesStablePublicErrors \
    Client_ExposesConnectionOwnership \
    StartupContract_NeverReplacesOrQueues \
    ExactFlags_AreClassifiedAndMalformedFlagsAreRefused \
    PocMode_BlocksCriticalMutationEntrypoints; do
    grep -Fq "PASS  $security_test" "$WORK_DIR/csharp.log" \
        || fail "missing security regression: $security_test"
done

echo "== i18n and boot-script scenarios =="
./scripts/i18n-check.sh --summary | tee "$WORK_DIR/i18n.log"
grep -Fq '0 missing, 0 dead, 0 drift' "$WORK_DIR/i18n.log" || fail "i18n audit failed"
bash install/tests/test-ghelper-gpu-boot.sh 2>&1 | tee "$WORK_DIR/boot.log"
grep -Fq 'Total: 93   Passed: 93   Failed: 0' "$WORK_DIR/boot.log" \
    || fail "boot-script scenarios failed or changed count"
assert_install_quarantine

echo "== Audio integration tests (offline container) =="
audio_parser_bin="audio-helper/tests/cs/bin/Debug/net10.0/TestAudioPipeline"
mkdir -p "$(dirname "$audio_parser_bin")"
printf '#!/bin/sh\nexit 73\n' > "$audio_parser_bin"
chmod +x "$audio_parser_bin"
echo "Starting verifier-owned PipeWire/Pulse/WirePlumber inside the offline container."
"${container[@]}" "$image_id" dbus-run-session -- bash -lc '
        set -euo pipefail
        export XDG_RUNTIME_DIR=/tmp/ghelper-pipewire
        mkdir -p "$XDG_RUNTIME_DIR"
        chmod 700 "$XDG_RUNTIME_DIR"
        pipewire >/tmp/ghelper-pipewire.log 2>&1 & pipewire_pid=$!
        wireplumber_pid=""
        loopback_pid=""
        cleanup_audio_servers() {
            kill "$loopback_pid" "$pulse_pid" "$wireplumber_pid" "$pipewire_pid" \
                2>/dev/null || true
        }
        pulse_pid=""
        trap cleanup_audio_servers EXIT
        for _ in $(seq 1 100); do
            [[ -S "$XDG_RUNTIME_DIR/pipewire-0" ]] && break
            sleep 0.1
        done
        [[ -S "$XDG_RUNTIME_DIR/pipewire-0" ]]
        wireplumber >/tmp/ghelper-wireplumber.log 2>&1 & wireplumber_pid=$!
        pipewire-pulse >/tmp/ghelper-pipewire-pulse.log 2>&1 & pulse_pid=$!
        for _ in $(seq 1 100); do
            [[ -S "$XDG_RUNTIME_DIR/pulse/native" ]] && break
            sleep 0.1
        done
        [[ -S "$XDG_RUNTIME_DIR/pulse/native" ]]
        pw-loopback \
            --capture-props="media.class=Audio/Sink node.name=ghelper_ci_sink" \
            --playback-props="media.class=Audio/Source node.name=ghelper_ci_source node.description=GHelper_CI_Microphone" \
            >/tmp/ghelper-loopback.log 2>&1 & loopback_pid=$!
        for _ in $(seq 1 100); do
            pw-cli ls Node 2>/dev/null | grep -q "node.name = \"ghelper_ci_source\"" && break
            sleep 0.1
        done
        pw-cli ls Node 2>/dev/null | grep -q "node.name = \"ghelper_ci_source\""
        ./audio-helper/test.sh
    ' 2>&1 | tee "$WORK_DIR/audio.log"
grep -Fq '===== 12 pass, 0 fail =====' "$WORK_DIR/audio.log" \
    || fail "audio integration suite failed or changed count"
[[ ! -e .config/ghelper/config.json ]] \
    || fail "CLI regression test leaked configuration into the repository"

(cd audio-helper && make clean >/dev/null 2>&1) || true
assert_no_generated_vendor_outputs
assert_install_quarantine
git diff --check

cd "$REPO_DIR"
assert_no_ignored_build_outputs
[[ "$(git status --porcelain=v1 --untracked-files=all)" == "$reviewed_guard_status" \
   && "$(ghelper_worktree_diff_hash "$REPO_DIR")" == "$reviewed_guard_hash" \
   && "$(reviewed_checkout_snapshot)" == "$reviewed_checkout_hash" ]] \
    || fail "verifier mutated the reviewed checkout"
[[ "$(tree_snapshot "$host_cache_root")" == "$host_cache_snapshot_before" ]] \
    || fail "verifier mutated the persistent host build/NuGet cache"

echo "Phase 1 acceptance passed."
echo "AOT SHA-256: $hash_two"
echo "Build image: $image_id"
