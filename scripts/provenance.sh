#!/usr/bin/env bash

GHELPER_UPSTREAM_COMMIT="9e99e21153a7cf75dd1682425bf1bd3d1b1de43a"
GHELPER_FORK_VERSION="1.0.90-x13.1"

ghelper_worktree_diff_hash() {
    local repo_dir="$1"
    {
        git -C "$repo_dir" diff --binary --no-ext-diff HEAD --
        while IFS= read -r -d '' path; do
            printf 'untracked\0%s\0%s\0' "$path" "$(stat -c %a "$repo_dir/$path")"
            sha256sum "$repo_dir/$path" | cut -d' ' -f1
        done < <(git -C "$repo_dir" ls-files --others --exclude-standard -z | LC_ALL=C sort -z)
    } | sha256sum | cut -d' ' -f1
}

ghelper_set_provenance() {
    local repo_dir="$1"
    local environment_hash="${2:-}"
    local dirty_review="${GHELPER_DIRTY_REVIEW:-0}"
    local head diff_hash

    head="$(git -C "$repo_dir" rev-parse HEAD)"
    git -C "$repo_dir" merge-base --is-ancestor "$GHELPER_UPSTREAM_COMMIT" "$head" || {
        echo "ERROR: upstream v1.0.90 is not an ancestor of HEAD" >&2
        return 1
    }

    if [[ -n "$(git -C "$repo_dir" status --porcelain=v1 --untracked-files=all)" ]]; then
        if [[ "$dirty_review" != "1" ]]; then
            echo "ERROR: dirty tree refused; release/default builds require a clean fork commit" >&2
            echo "For review-only artifacts set GHELPER_DIRTY_REVIEW=1." >&2
            return 1
        fi
        diff_hash="$(ghelper_worktree_diff_hash "$repo_dir")"
        GHELPER_BUILD_MODE="dirty-review"
        GHELPER_DIFF_HASH="$diff_hash"
        GHELPER_BUILD_PROVENANCE="dirty.${diff_hash}.base.${head}"
    else
        GHELPER_BUILD_MODE="clean"
        GHELPER_DIFF_HASH=""
        GHELPER_BUILD_PROVENANCE="$head"
    fi

    GHELPER_BUILD_ENV_HASH="$environment_hash"
    GHELPER_INFORMATIONAL_VERSION="${GHELPER_FORK_VERSION}+${GHELPER_BUILD_PROVENANCE}"
    if [[ -n "$environment_hash" ]]; then
        [[ "$environment_hash" =~ ^[0-9a-f]{64}$ ]] || {
            echo "ERROR: invalid build-environment SHA-256" >&2
            return 1
        }
        GHELPER_INFORMATIONAL_VERSION+=".env.${environment_hash}"
    fi
    export GHELPER_BUILD_MODE GHELPER_DIFF_HASH GHELPER_BUILD_PROVENANCE
    export GHELPER_BUILD_ENV_HASH GHELPER_INFORMATIONAL_VERSION
}
