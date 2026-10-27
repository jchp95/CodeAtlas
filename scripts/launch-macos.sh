#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# Fail closed: no `swift run` call is allowed before every test and build passes.
"$ROOT/scripts/verify-before-launch.sh"
cd "$ROOT/macOSApp"
exec swift run --skip-build
