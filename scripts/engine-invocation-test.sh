#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BINARY="$(mktemp -t codeatlas-engine-resolver.XXXXXX)"
trap 'rm -f "$BINARY"' EXIT
swiftc "$ROOT/macOSApp/Sources/CodeAtlas/Infrastructure/EngineInvocationResolver.swift" \
  "$ROOT/tests/swift/EngineInvocationResolverRegressionTests.swift" -o "$BINARY"
"$BINARY"
