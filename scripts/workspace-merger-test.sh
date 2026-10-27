#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/codeatlas-workspace-merger.XXXXXX")"
trap 'rm -rf "$TEMP_DIR"' EXIT
swiftc \
  "$ROOT/macOSApp/Sources/CodeAtlas/Features/Architecture/Models/Models.swift" \
  "$ROOT/macOSApp/Sources/CodeAtlas/Features/Workspace/WorkspaceSnapshotMerger.swift" \
  "$ROOT/tests/WorkspaceSnapshotMergerTests.swift" \
  -o "$TEMP_DIR/workspace-merger-tests"
"$TEMP_DIR/workspace-merger-tests"
