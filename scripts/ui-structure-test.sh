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
if grep -q '\.preferredColorScheme(\.dark)' "$APP/CodeAtlasApp.swift"; then
  fail "app still forces dark mode"
fi
while IFS= read -r -d '' source; do
  swiftc -frontend -parse "$source" >/dev/null || fail "Swift syntax parse failed: ${source#"$APP"/}"
done < <(find "$APP" -type f -name '*.swift' -print0)

echo "UI structure tests passed: adaptive theme, shared glass design, grouped navigation, focused views, footer, and Swift source syntax."
