#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/macOSApp/Sources/CodeAtlas"
fail() { echo "UI structure test failed: $1" >&2; exit 1; }

DESIGN="$APP/Views/DesignSystem/DesignSystem.swift"
[[ -f "$DESIGN" ]] || fail "missing shared adaptive design system"
grep -q 'struct AtlasBackdrop' "$DESIGN" || fail "missing shared blurred backdrop"
grep -q 'struct AtlasGlassSurface' "$DESIGN" || fail "missing glass surface modifier"
grep -q '@AppStorage("appearanceMode")' "$APP/CodeAtlasApp.swift" || fail "appearance setting is not persisted"
grep -q 'case "light": return .light' "$APP/CodeAtlasApp.swift" || fail "light theme is not supported"
grep -q 'case "dark": return .dark' "$APP/CodeAtlasApp.swift" || fail "dark theme is not supported"
grep -q 'DisclosureGroup(isExpanded: \$architectureMenuExpanded)' "$APP/ContentView.swift" || fail "architecture submenu is missing"
grep -q 'DisclosureGroup(isExpanded: \$developmentMenuExpanded)' "$APP/ContentView.swift" || fail "development submenu is missing"
grep -q 'DisclosureGroup(isExpanded: \$operationsMenuExpanded)' "$APP/ContentView.swift" || fail "operations submenu is missing"
grep -q 'case "Resumen"' "$APP/ContentView+Navigation.swift" || fail "workspace summary is not separated"
grep -q 'case "Relaciones"' "$APP/ContentView+Navigation.swift" || fail "relations view is not separated"
grep -q 'case "Análisis"' "$APP/ContentView+Navigation.swift" || fail "analysis/evidence view is not separated"
grep -q 'var statusBar: some View' "$APP/ContentView+Overview.swift" || fail "main footer/status bar is missing"
[[ -f "$APP/Views/Build/BuildConsoleView.swift" && -f "$APP/Features/Build/BuildConsoleModel.swift" ]] || fail "build and test console is missing"
[[ -f "$APP/Views/Git/GitWorkbenchView.swift" ]] || fail "Git branch workbench is missing"
grep -q 'GitWorkbenchView' "$APP/ContentView.swift" || fail "Git workbench is not reachable from the navigation shell"
[[ -f "$APP/Views/StackPilot/StackPilotProfilesView.swift" && -f "$APP/Views/StackPilot/StackPilotDiagnosticsView.swift" ]] || fail "independent StackPilot profile/diagnostic views are missing"
grep -q 'Completar con IA' "$APP/Views/CodeEditor/CodeExplorerView.swift" || fail "AI code completion entry point is missing"
grep -q 'CodeFormatter.command' "$APP/Views/CodeEditor/CodeExplorerView.swift" || fail "extension-specific formatter action is missing"
[[ -f "$APP/Infrastructure/VSCodeBridge/VSCodeBridgeService.swift" ]] || fail "native VS Code bridge is missing"
grep -q 'vscodeBridge.start()' "$APP/CodeAtlasApp.swift" || fail "VS Code bridge does not start with the app"
[[ -f "$ROOT/vscode-extension/extension.js" && -f "$ROOT/vscode-extension/package.json" ]] || fail "installable VS Code extension sources are missing"
[[ -f "$APP/Views/CodeEditor/CodeExplorerView.swift" ]] || fail "native code explorer is missing"
grep -q 'HSplitView' "$APP/Views/CodeEditor/CodeExplorerView.swift" || fail "code editor and right inspector layout is missing"
grep -q 'confirmationDialog("Hay cambios sin guardar"' "$APP/Views/CodeEditor/CodeExplorerView.swift" || fail "editor does not protect unsaved changes when opening another file"
if grep -q '\.preferredColorScheme(\.dark)' "$APP/CodeAtlasApp.swift"; then
  fail "app still forces dark mode"
fi
if ! find "$APP" -type f -name '*.swift' -print0 | xargs -0 swiftc -frontend -parse >/dev/null; then
  fail "Swift source syntax parsing failed; run swiftc -frontend -parse on the affected source files for details."
fi

echo "UI structure tests passed: adaptive theme, shared glass design, grouped navigation, IDE tools, footer, and Swift source syntax."
