#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BINARY="$(mktemp -t codeatlas-process-execution-tests.XXXXXX)"
trap 'rm -f "$BINARY"' EXIT
swiftc "$ROOT/macOSApp/Sources/CodeAtlas/Infrastructure/ProcessExecutionService.swift" \
  "$ROOT/tests/swift/ProcessExecutionRegressionTests.swift" -o "$BINARY"
"$BINARY"
echo "Process-execution test passed: process lifecycle is cancellable and output is bounded."
