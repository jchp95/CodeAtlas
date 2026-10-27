#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ENGINE="$ROOT/engine/CodeAtlas.Engine"
APP="$ROOT/macOSApp/Sources/CodeAtlas"
fail() { echo "Vertical-slice structure test failed: $1" >&2; exit 1; }

for legacy in "$ENGINE/StackPilot.cs" "$ENGINE/HttpTesting.cs" "$ENGINE/BusinessFlowAnalyzer.cs"; do
  [[ ! -e "$legacy" ]] || fail "legacy god file still exists: ${legacy#"$ROOT"/}"
done
[[ -f "$ENGINE/Program.cs" ]] || fail "CLI entry point missing"
[[ -f "$ENGINE/Features/Architecture/Scanning/RepositoryScanner.cs" ]] || fail "architecture scanner feature missing"
[[ -f "$ENGINE/Features/HttpTesting/Execution/HttpRequestExecutor.cs" ]] || fail "HTTP execution slice missing"
[[ -f "$ENGINE/Features/StackPilot/StackPilotCli.ServiceLifecycle.cs" ]] || fail "StackPilot lifecycle slice missing"
[[ -f "$ENGINE/Features/BusinessFlows/Analysis/BusinessFlowAnalyzer.TraceExpansion.cs" ]] || fail "business-flow trace slice missing"
grep -q "TryTraceSourceLocalMethod" "$ENGINE/Features/BusinessFlows/Analysis/BusinessFlowAnalyzer.TraceExpansion.cs" || fail "source-backed fallback for unresolved local method calls is missing"
[[ -f "$APP/ContentView.swift" && -f "$APP/ContentView+Workspace.swift" ]] || fail "ContentView composition/workspace slices missing"
[[ -f "$APP/Views/HTTPTesting/EndpointHTTPExplorerView+Execution.swift" ]] || fail "HTTP view execution slice missing"
[[ -f "$APP/Infrastructure/EngineClient+Process.swift" ]] || fail "engine process adapter slice missing"
[[ -f "$APP/Views/Architecture/Diagram/ArchitectureDiagramLayout.swift" ]] || fail "diagram layout component missing"
[[ -f "$APP/Views/BusinessFlows/Components/BusinessFlowCard.swift" ]] || fail "business-flow card component missing"
[[ -f "$APP/Views/BusinessFlows/Components/DistributedFlowStepRow.swift" ]] || fail "distributed-flow row component missing"
[[ -f "$ENGINE/Features/HttpTesting/Catalog/HttpTestingCatalogBuilder.FastEndpoints.cs" ]] || fail "FastEndpoints discovery slice missing"
[[ -f "$APP/Views/CodeEditor/CodeFileNode.swift" && -f "$APP/Views/CodeEditor/NativeCodeEditor.swift" ]] || fail "native code editor slice missing"
[[ -f "$APP/Features/Build/BuildConsoleModel.swift" && -f "$APP/Infrastructure/CodeFormatter.swift" ]] || fail "build/formatter application services are missing"
[[ -f "$APP/Infrastructure/CommandRunner.swift" ]] || fail "external command runner is missing"
[[ -f "$APP/Infrastructure/LanguageServices/LanguageServerClient.swift" ]] || fail "language server client is missing from infrastructure"
[[ -f "$APP/Features/Code/LanguageServices/LanguageServiceManager.swift" ]] || fail "language service manager feature is missing"
[[ -f "$APP/Features/Code/LanguageServices/LSPFrameCodec.swift" ]] || fail "LSP framing codec is missing"

while IFS= read -r -d '' source; do
  bytes=$(wc -c < "$source")
  (( bytes <= 30000 )) || fail "source file exceeds 30 KB: ${source#"$ROOT"/} ($bytes bytes)"
done < <(find "$ENGINE" "$APP" -type f \( -name '*.cs' -o -name '*.swift' \) -print0)

echo "Vertical-slice structure tests passed: feature owners are present, former god files are split, and no source file exceeds 30 KB."
