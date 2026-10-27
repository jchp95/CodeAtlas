#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/codeatlas-model-tests.XXXXXX")"
trap 'rm -rf "$TEMP_DIR"' EXIT

swiftc \
  "$ROOT/macOSApp/Sources/CodeAtlas/Features/Architecture/Models/Models.swift" \
  "$ROOT/tests/ModelCompatibilityTests.swift" \
  -o "$TEMP_DIR/model-compatibility-tests"

"$TEMP_DIR/model-compatibility-tests"
