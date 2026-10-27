#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TMP="$(mktemp -d -t code-atlas-symbol-index.XXXXXX)"
trap 'rm -rf "$TMP"' EXIT
cp "$ROOT/tests/swift/CodeSymbolIndexTests.swift" "$TMP/main.swift"
swiftc "$ROOT/macOSApp/Sources/CodeAtlas/Views/CodeEditor/CodeSymbolIndex.swift" "$TMP/main.swift" -o "$TMP/code-symbol-index-tests"
"$TMP/code-symbol-index-tests"
