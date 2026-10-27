#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/codeatlas-clean-layers.XXXXXX")"
trap 'rm -rf "$TEMP_DIR"' EXIT
swiftc \
  "$ROOT/macOSApp/Sources/CodeAtlas/Features/Architecture/Models/Models.swift" \
  "$ROOT/macOSApp/Sources/CodeAtlas/Features/Architecture/Grouping/CleanArchitectureLayerGrouper.swift" \
  "$ROOT/tests/CleanArchitectureLayerGrouperTests.swift" \
  -o "$TEMP_DIR/clean-architecture-tests"
"$TEMP_DIR/clean-architecture-tests"
