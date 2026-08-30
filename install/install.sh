#!/usr/bin/env bash
set -euo pipefail

echo "ERROR: upstream installer disabled in the X13 hardened fork." >&2
echo "The hardened package path is under development; this script cannot change the system." >&2
exit 64
