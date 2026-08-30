#!/usr/bin/env bash
# Phase 09: C# AudioFrame parser against live helper.
# Always clean-builds tests/cs/ from the current checkout, then runs it.
# Restore is a separate locked verifier step; this test must never reuse a
# stale parser binary from a previous checkout.
set -euo pipefail
. "$(dirname "$0")/lib.sh"

require dotnet || { echo "dotnet SDK not installed - skipping"; exit 0; }

CS_DIR="$(dirname "$0")/cs"
BIN="$CS_DIR/bin/Debug/net10.0/TestAudioPipeline"
if ! rm -rf "$CS_DIR/bin" "$CS_DIR/obj/Debug"; then
    echo "could not remove stale C# parser intermediates"
    exit 1
fi
if ! (cd "$CS_DIR" && dotnet build -c Debug --nologo -v q --no-restore) \
    >"$TMP/dotnet.log" 2>&1; then
    echo "dotnet clean build failed:"
    tail -30 "$TMP/dotnet.log"
    exit 1
fi

# Pre-emptively reap any stragglers; the C# program spawns its own helper.
pkill -9 -x ghelper-audio 2>/dev/null || true
sleep 0.2

GHELPER_AUDIO_BIN="$GHELPER_AUDIO_BIN" "$BIN"
