# Shared bash helpers for ghelper-audio test phases.
# Sourced by every tests/NN_*.sh.
#
# Provides:
#   spawn_helper       - starts helper with persistent stdin via FIFO; sets HELPER_PID, HELPER_STDIN, HELPER_OUT, HELPER_ERR
#   stop_helper        - graceful QUIT then SIGKILL, removes FIFO
#   wait_for_node      - poll pw-cli for our node id
#   link_default_mic   - pw-link default source to ghelper-audio:input, tolerating either port name
#   send_cmd           - echo a line to the helper's stdin FIFO
#   require            - assert a command exists, fail phase otherwise

set -uo pipefail

: "${TMP:?TMP not set; source from test.sh}"
: "${GHELPER_AUDIO_BIN:?bin path not set}"

require() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "missing required tool: $1" >&2
        return 1
    fi
}

spawn_helper() {
    HELPER_STDIN="$TMP/stdin.$$.$RANDOM"
    HELPER_OUT="$TMP/out.$$.$RANDOM"
    HELPER_ERR="$TMP/err.$$.$RANDOM"
    mkfifo "$HELPER_STDIN"
    # Open the fifo for both read and write on the same fd. This is the
    # non-blocking trick on Linux: write-only or read-only open on a fifo
    # would block until the other side appears, but read-write open
    # (O_RDWR) succeeds immediately without a peer. We use fd 9 to write
    # commands later via send_cmd.
    exec 9<>"$HELPER_STDIN"
    # Tests inspect frames while the helper is still running. Disable stdio
    # buffering so a forced cleanup cannot hide otherwise valid frames in the
    # process-local buffer.
    stdbuf -o0 "$GHELPER_AUDIO_BIN" <"$HELPER_STDIN" >"$HELPER_OUT" 2>"$HELPER_ERR" &
    HELPER_PID=$!
    # Wait for "ready" line (helper writes it to stderr right after init).
    local i=0
    while (( i++ < 50 )); do
        if grep -q "ready" "$HELPER_ERR" 2>/dev/null; then
            return 0
        fi
        if ! kill -0 "$HELPER_PID" 2>/dev/null; then
            echo "helper exited prematurely:" >&2
            cat "$HELPER_ERR" >&2
            return 1
        fi
        sleep 0.05
    done
    echo "helper did not become ready within 2.5s" >&2
    cat "$HELPER_ERR" >&2
    return 1
}

stop_helper() {
    if [[ -n "${HELPER_PID:-}" ]] && kill -0 "$HELPER_PID" 2>/dev/null; then
        # Try graceful first.
        send_cmd "QUIT" 2>/dev/null || true
        local i=0
        while (( i++ < 10 )) && kill -0 "$HELPER_PID" 2>/dev/null; do
            sleep 0.05
        done
        kill -9 "$HELPER_PID" 2>/dev/null || true
        wait "$HELPER_PID" 2>/dev/null || true
    fi
    # Close fifo write side.
    exec 9>&- 2>/dev/null || true
    rm -f "${HELPER_STDIN:-}"
    HELPER_PID=""
}

send_cmd() {
    [[ -z "${HELPER_STDIN:-}" ]] && return 1
    printf '%s\n' "$1" >&9
}

wait_for_node() {
    local name="${1:-ghelper-audio}"
    local timeout="${2:-3.0}"
    local started=$EPOCHREALTIME
    while :; do
        if pw-cli ls Node 2>/dev/null | grep -q "node.name = \"$name\""; then
            return 0
        fi
        local now=$EPOCHREALTIME
        awk -v s="$started" -v n="$now" -v t="$timeout" 'BEGIN { exit !(n - s > t) }' && return 1
        sleep 0.1
    done
}

# Get our node id from pw-cli (first hit).
get_node_id() {
    local name="${1:-ghelper-audio}"
    pw-cli ls Node 2>/dev/null | awk -v n="$name" '
        /^\tid [0-9]+, type PipeWire:Interface:Node/ { id=$2; sub(/,$/, "", id) }
        $0 ~ "node.name = \"" n "\"" { print id; exit }
    '
}

# The capture stream sets node.autoconnect=true so wireplumber wires it to
# the default mic automatically. wait_for_capture_link waits for that link
# to appear so subsequent tests observe real audio flowing.
wait_for_capture_link() {
    local timeout="${1:-5.0}"
    local started=$EPOCHREALTIME
    while :; do
        if pw-link -l 2>/dev/null | grep -q "ghelper-audio-capture"; then
            return 0
        fi
        local now=$EPOCHREALTIME
        awk -v s="$started" -v n="$now" -v t="$timeout" 'BEGIN { exit !(n - s > t) }' && return 1
        sleep 0.1
    done
}

# Back-compat shim: tests previously called link_default_mic. With the
# two-stream architecture the capture stream auto-connects, so this just
# waits for the link to appear.
link_default_mic() {
    wait_for_capture_link 3
}

assert() {
    if eval "$1"; then return 0; fi
    echo "ASSERT failed: $1" >&2
    return 1
}
