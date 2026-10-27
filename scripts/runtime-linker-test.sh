#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT

swiftc \
  "$ROOT/macOSApp/Sources/CodeAtlas/Features/Architecture/Models/Models.swift" \
  "$ROOT/macOSApp/Sources/CodeAtlas/Features/StackPilot/Models/StackPilotModels.swift" \
  "$ROOT/macOSApp/Sources/CodeAtlas/Features/Architecture/Runtime/ArchitectureRuntimeLinker.swift" \
  "$ROOT/tests/RuntimeLinkerTests.swift" \
  -o "$TEMP_DIR/runtime-linker-tests"

"$TEMP_DIR/runtime-linker-tests"
