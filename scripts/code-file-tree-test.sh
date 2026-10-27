#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/codeatlas-tree-test.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT
swiftc "$ROOT/macOSApp/Sources/CodeAtlas/Views/CodeEditor/CodeFileNode.swift" \
  "$ROOT/tests/swift/CodeFileNodeTests.swift" -o "$TMP/code-file-tree-tests"
"$TMP/code-file-tree-tests"
