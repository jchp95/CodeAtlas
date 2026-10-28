#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BINARY="$(mktemp -t codeatlas-utf8-stream-tests.XXXXXX)"
trap 'rm -f "$BINARY"' EXIT
swiftc "$ROOT/macOSApp/Sources/CodeAtlas/Features/Build/UTF8StreamDecoder.swift" \
  "$ROOT/tests/swift/UTF8StreamDecoderTests.swift" -o "$BINARY"
"$BINARY"
