#!/usr/bin/env bash

# Exact inputs that define the locked NuGet closure. Keep this list explicit:
# adding a project, central props/targets file, lock file, or NuGet config must
# deliberately change the cache identity.
ghelper_cache_input_files() {
    printf '%s\n' \
        global.json \
        Directory.Build.props \
        NuGet.Config \
        src/GHelper.Linux.csproj \
        src/packages.lock.json \
        tests/GHelper.Linux.Tests/GHelper.Linux.Tests.csproj \
        tests/GHelper.Linux.Tests/packages.lock.json \
        audio-helper/tests/cs/TestAudioPipeline.csproj \
        audio-helper/tests/cs/packages.lock.json
}

ghelper_cache_input_hash() {
    local repo_root="$1" relative
    [[ "$repo_root" == /* && -d "$repo_root" && ! -L "$repo_root" ]] || return 1
    while IFS= read -r relative; do
        [[ -f "$repo_root/$relative" && ! -L "$repo_root/$relative" ]] || {
            echo "ERROR: missing or unsafe cache input: $relative" >&2
            return 1
        }
        printf '%s\0' "$relative"
        sha256sum "$repo_root/$relative" | cut -d' ' -f1
    done < <(ghelper_cache_input_files)
}

ghelper_cache_input_sha256() {
    ghelper_cache_input_hash "$1" | sha256sum | cut -d' ' -f1
}

# Emit the exact package-id/version closure described by all three lock files.
# The three implicit runtime packs are selected at the exact ILCompiler version
# resolved by the main lock file. Python is used only as a strict JSON parser.
ghelper_nuget_closure() {
    local repo_root="$1"
    python3 - "$repo_root" <<'PY'
import json
import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1])
locks = [
    root / "src/packages.lock.json",
    root / "tests/GHelper.Linux.Tests/packages.lock.json",
    root / "audio-helper/tests/cs/packages.lock.json",
]
closure = set()
ilcompiler_version = None
for lock in locks:
    data = json.loads(lock.read_text(encoding="utf-8"))
    if data.get("version") != 1 or not isinstance(data.get("dependencies"), dict):
        raise SystemExit(f"invalid NuGet lock file: {lock}")
    for framework in data["dependencies"].values():
        if not isinstance(framework, dict):
            raise SystemExit(f"invalid dependency graph: {lock}")
        for package_id, record in framework.items():
            version = record.get("resolved") if isinstance(record, dict) else None
            normalized = package_id.lower()
            if not re.fullmatch(r"[a-z0-9_.-]+", normalized or ""):
                raise SystemExit(f"unsafe package id in {lock}: {package_id}")
            if not isinstance(version, str) or not re.fullmatch(r"[0-9A-Za-z.+-]+", version):
                raise SystemExit(f"unsafe package version in {lock}: {package_id}")
            closure.add((normalized, version))
            if normalized == "runtime.linux-x64.microsoft.dotnet.ilcompiler":
                ilcompiler_version = version
if ilcompiler_version is None:
    raise SystemExit("locked ILCompiler version is missing")
for package_id in (
    "microsoft.aspnetcore.app.runtime.linux-x64",
    "microsoft.netcore.app.runtime.linux-x64",
    "microsoft.netcore.app.runtime.nativeaot.linux-x64",
):
    closure.add((package_id, ilcompiler_version))
for package_id, version in sorted(closure):
    print(f"{package_id}\t{version}")
PY
}

# Reject links, devices, sockets, FIFOs, and any other non-file/non-directory
# node before a cache is copied, mounted, hashed, or consumed.
ghelper_nuget_validate_tree() {
    local cache_root="$1" unsafe
    [[ "$cache_root" == /* && -d "$cache_root" && ! -L "$cache_root" ]] || {
        echo "ERROR: NuGet cache root must be an absolute real directory" >&2
        return 1
    }
    unsafe="$(find -P "$cache_root" -mindepth 1 ! \( -type d -o -type f \) -print -quit)"
    [[ -z "$unsafe" ]] || {
        echo "ERROR: unsafe NuGet cache node: $unsafe" >&2
        return 1
    }
}

# Copy only the exact locked closure. Unrelated packages in a shared cache are
# ignored, so a contaminated cache cannot change the private build environment.
ghelper_nuget_materialize_closure() {
    local repo_root="$1" source_root="$2" destination_root="$3"
    local package_id version source_version destination_package
    ghelper_nuget_validate_tree "$source_root" || return 1
    [[ "$destination_root" == /* && ! -e "$destination_root" && ! -L "$destination_root" ]] || {
        echo "ERROR: private NuGet destination must be a new absolute path" >&2
        return 1
    }
    mkdir -m 700 "$destination_root"
    while IFS=$'\t' read -r package_id version; do
        source_version="$source_root/$package_id/$version"
        destination_package="$destination_root/$package_id"
        [[ -d "$source_version" && ! -L "$source_version" ]] || {
            echo "ERROR: locked package missing from shared cache: $package_id/$version" >&2
            return 1
        }
        mkdir -m 700 -p "$destination_package"
        cp -a --reflink=auto "$source_version" "$destination_package/$version"
    done < <(ghelper_nuget_closure "$repo_root")
    ghelper_nuget_validate_tree "$destination_root"
}

# Deterministic manifest of every regular file in a validated private cache.
ghelper_nuget_manifest() {
    local cache_root="$1" path relative
    ghelper_nuget_validate_tree "$cache_root" || return 1
    while IFS= read -r -d '' path; do
        relative="${path#"$cache_root"/}"
        printf 'file\t%s\t%s\t%s\t%s\n' \
            "$(stat -c %a "$path")" "$(stat -c %s "$path")" \
            "$(sha256sum "$path" | cut -d' ' -f1)" "$relative"
    done < <(find -P "$cache_root" -type f -print0 | LC_ALL=C sort -z)
}

ghelper_nuget_manifest_sha256() {
    ghelper_nuget_manifest "$1" | sha256sum | cut -d' ' -f1
}
