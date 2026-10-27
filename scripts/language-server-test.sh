#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TMP="$(mktemp -d -t code-atlas-lsp.XXXXXX)"
trap 'rm -rf "$TMP"' EXIT
command -v swiftc >/dev/null 2>&1 || { echo "LSP tests require swiftc." >&2; exit 1; }
command -v node >/dev/null 2>&1 || { echo "LSP integration test requires Node.js." >&2; exit 1; }
SWIFT="$ROOT/macOSApp/Sources/CodeAtlas"
TARGET="$TMP/Definition.cs"
printf 'namespace Test;\n\nclass Definition { }\n' > "$TARGET"
swiftc -parse-as-library \
  "$SWIFT/Features/Code/LanguageServices/LanguageServerLaunchConfiguration.swift" \
  "$SWIFT/Features/Code/LanguageServices/LanguageServerModels.swift" \
  "$SWIFT/Features/Code/LanguageServices/LSPFrameCodec.swift" \
  "$SWIFT/Infrastructure/LanguageServices/LanguageServerClient.swift" \
  "$ROOT/tests/swift/LanguageServicesRegressionTests.swift" \
  -o "$TMP/language-services-tests"
"$TMP/language-services-tests" "$ROOT/tests/swift/fake-language-server.cjs" "$TARGET"
echo "Language-server tests passed: frame codec, safety limits, process handshake, server requests and real stdio JSON-RPC operations."
