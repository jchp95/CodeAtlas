#!/usr/bin/env bash
# Compiles and links every Swift source of the app directly with swiftc, bypassing SwiftPM. It proves the code builds against the installed
# macOS SDK even when SwiftPM itself cannot run (a Command Line Tools install whose PackageDescription library does not match its interface
# makes `swift build` fail before compiling anything). It does not replace `swift build`, which stays the gate in verify-before-launch.sh.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$(mktemp "${TMPDIR:-/tmp}/codeatlas-swiftc.XXXXXX")"
trap 'rm -f "$OUT"' EXIT
find "$ROOT/macOSApp/Sources/CodeAtlas" -name '*.swift' -print0 | sort -z \
  | xargs -0 swiftc -swift-version 5 -target "$(uname -m)-apple-macos14.0" -parse-as-library -module-name CodeAtlas -o "$OUT"
[[ -x "$OUT" ]] || { echo "Swift compile check failed: no executable was produced." >&2; exit 1; }
echo "Swift compile check passed: $(find "$ROOT/macOSApp/Sources/CodeAtlas" -name '*.swift' | wc -l | tr -d ' ') files compiled and linked without SwiftPM."
