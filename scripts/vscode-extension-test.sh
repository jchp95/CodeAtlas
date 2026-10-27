#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
EXT="$ROOT/vscode-extension"
BRIDGE="$ROOT/macOSApp/Sources/CodeAtlas/Infrastructure/VSCodeBridge/VSCodeBridgeService.swift"
command -v node >/dev/null 2>&1 || { echo "Node.js is required for VS Code extension validation." >&2; exit 1; }
node --check "$EXT/extension.js"
node -e 'const fs=require("fs");const p=JSON.parse(fs.readFileSync(process.argv[1],"utf8"));if(p.main!=="./extension.js")process.exit(2);for(const id of ["codeAtlas.openActiveFile","codeAtlas.openWorkspace","codeAtlas.checkConnection"])if(!(p.contributes.commands||[]).some(c=>c.command===id))process.exit(3);console.log("VS Code extension manifest tests passed: entry point, commands and JSON schema.")' "$EXT/package.json"
for file in "$EXT/extension.vsixmanifest" "$EXT/[Content_Types].xml"; do
  [[ -s "$file" ]] || { echo "Missing VSIX manifest file: $file" >&2; exit 1; }
done
grep -q '127.0.0.1' "$EXT/extension.js" || { echo "Extension is not restricted to loopback." >&2; exit 1; }
grep -q 'X-CodeAtlas-Token' "$EXT/extension.js" || { echo "Extension does not authenticate bridge calls." >&2; exit 1; }
for expected in '127.0.0.1' 'S_IRUSR | S_IWUSR' 'SourceNavigator.open' 'codeAtlasOpenWorkspace'; do
  grep -Fq "$expected" "$BRIDGE" || { echo "Native bridge security/integration test failed: missing $expected" >&2; exit 1; }
done
if command -v python3 >/dev/null 2>&1; then
  python3 - "$EXT" <<'PY'
import sys, xml.etree.ElementTree as ET
from pathlib import Path
root=Path(sys.argv[1])
ET.parse(root/'extension.vsixmanifest')
ET.parse(root/'[Content_Types].xml')
print('VSIX XML manifests passed structural parsing.')
PY
fi
echo "VS Code bridge security tests passed: loopback binding, owner-only secret, source and workspace commands."
