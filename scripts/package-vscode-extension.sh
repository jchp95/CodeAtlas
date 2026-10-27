#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
EXT="$ROOT/vscode-extension"
OUT="$ROOT/artifacts/vscode-extension"
VERSION="$(node -p "require('$EXT/package.json').version")"
mkdir -p "$OUT"
VSIX="$OUT/CodeAtlas-VSCode-Bridge-$VERSION.vsix"
rm -f "$VSIX"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$TMP/extension"
cp "$EXT/package.json" "$EXT/extension.js" "$TMP/extension/"
cp "$EXT/extension.vsixmanifest" "$TMP/extension.vsixmanifest"
cp "$EXT/[Content_Types].xml" "$TMP/[Content_Types].xml"
(cd "$TMP" && zip -qr "$VSIX" .)
unzip -t "$VSIX" >/dev/null
printf 'VS Code extension packaged: %s\n' "$VSIX"
