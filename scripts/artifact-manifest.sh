#!/usr/bin/env bash

GHELPER_ARTIFACT_MANIFEST_NAME=".ghelper-build-manifest-v1"
GHELPER_OUTPUT_OWNER_NAME=".ghelper-x13-output-owner-v1"

ghelper_artifact_validate_tree() {
    local artifact_root="$1" unsafe
    [[ "$artifact_root" == /* && -d "$artifact_root" && ! -L "$artifact_root" ]] || {
        echo "ERROR: artifact root must be an absolute real directory" >&2
        return 1
    }
    unsafe="$(find -P "$artifact_root" -mindepth 1 ! \( -type d -o -type f \) -print -quit)"
    [[ -z "$unsafe" ]] || {
        echo "ERROR: unsafe artifact node: $unsafe" >&2
        return 1
    }
}

ghelper_artifact_tree_manifest() {
    local artifact_root="$1" path relative
    ghelper_artifact_validate_tree "$artifact_root" || return 1
    while IFS= read -r -d '' path; do
        relative="${path#"$artifact_root"/}"
        case "$relative" in
            "$GHELPER_ARTIFACT_MANIFEST_NAME"|"$GHELPER_OUTPUT_OWNER_NAME") continue ;;
        esac
        printf 'file\t%s\t%s\t%s\t%s\n' \
            "$(stat -c %a "$path")" "$(stat -c %s "$path")" \
            "$(sha256sum "$path" | cut -d' ' -f1)" "$relative"
    done < <(find -P "$artifact_root" -type f -print0 | LC_ALL=C sort -z)
}

ghelper_artifact_tree_sha256() {
    ghelper_artifact_tree_manifest "$1" | sha256sum | cut -d' ' -f1
}

ghelper_render_artifact_manifest() {
    local artifact_root="$1" informational_version="$2" source_provenance="$3"
    local build_mode="$4" image_id="$5" image_input_hash="$6"
    local cache_input_hash="$7" nuget_manifest_hash="$8" environment_hash="$9"
    local tree_manifest tree_hash binary_hash
    [[ -f "$artifact_root/ghelper" && ! -L "$artifact_root/ghelper" ]] || return 1
    tree_manifest="$(ghelper_artifact_tree_manifest "$artifact_root")"
    tree_hash="$(printf '%s\n' "$tree_manifest" | sha256sum | cut -d' ' -f1)"
    binary_hash="$(sha256sum "$artifact_root/ghelper" | cut -d' ' -f1)"
    printf 'format=ghelper-phase1-artifact-manifest-v1\n'
    printf 'trust=local-reproducible-unsigned\n'
    printf 'informational_version=%s\n' "$informational_version"
    printf 'source_provenance=%s\n' "$source_provenance"
    printf 'build_mode=%s\n' "$build_mode"
    printf 'image_id=%s\n' "$image_id"
    printf 'image_input_sha256=%s\n' "$image_input_hash"
    printf 'cache_input_sha256=%s\n' "$cache_input_hash"
    printf 'nuget_manifest_sha256=%s\n' "$nuget_manifest_hash"
    printf 'environment_sha256=%s\n' "$environment_hash"
    printf 'artifact_sha256=%s\n' "$binary_hash"
    printf 'artifact_tree_sha256=%s\n' "$tree_hash"
    printf 'artifact_tree_manifest_begin\n%s\nartifact_tree_manifest_end\n' "$tree_manifest"
}

ghelper_write_artifact_manifest() {
    local artifact_root="$1" manifest_path
    manifest_path="$artifact_root/$GHELPER_ARTIFACT_MANIFEST_NAME"
    [[ ! -e "$manifest_path" && ! -L "$manifest_path" ]] || return 1
    ghelper_render_artifact_manifest "$@" > "$manifest_path"
    chmod 600 "$manifest_path"
}
