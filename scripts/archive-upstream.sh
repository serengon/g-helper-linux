#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
UPSTREAM_COMMIT="9e99e21153a7cf75dd1682425bf1bd3d1b1de43a"
EXPECTED_TREE="339975db848f2a0d08b27b49a45ed6606540c3da"
EXPECTED_SHA256="0ba1f34797978af2ff245312d36a687eb5d8316b692ca93b64e7cd0f5aeab88d"
OUTPUT="${1:-$REPO_DIR/dist/sources/g-helper-linux-1.0.90-upstream.tar.gz}"

actual_tree="$(git -C "$REPO_DIR" rev-parse "$UPSTREAM_COMMIT^{tree}")"
if [[ "$actual_tree" != "$EXPECTED_TREE" ]]; then
    echo "ERROR: upstream tree mismatch: $actual_tree" >&2
    exit 1
fi

mkdir -p "$(dirname "$OUTPUT")"
output_dir="$(cd "$(dirname "$OUTPUT")" && pwd)"
output_name="$(basename "$OUTPUT")"
tmp_output="$(mktemp "$output_dir/.${output_name}.tmp.XXXXXX")"
trap 'rm -f "$tmp_output"' EXIT

LC_ALL=C TZ=UTC git -C "$REPO_DIR" archive \
    --format=tar \
    --prefix=g-helper-linux-1.0.90/ \
    "$UPSTREAM_COMMIT" \
    | gzip -n -9 > "$tmp_output"

actual_sha256="$(sha256sum "$tmp_output" | cut -d' ' -f1)"
if [[ "$actual_sha256" != "$EXPECTED_SHA256" ]]; then
    echo "ERROR: archive SHA-256 mismatch: $actual_sha256" >&2
    exit 1
fi

mv -f "$tmp_output" "$OUTPUT"
trap - EXIT
printf '%s  %s\n' "$actual_sha256" "$OUTPUT"
