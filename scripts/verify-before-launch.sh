#!/usr/bin/env bash
# Fail-closed startup gate: the app is never launched unless the complete
# validation suite and both production builds succeed.
set -Eeuo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_DIR="$ROOT/macOSApp"
ENGINE_PROJECT="$ROOT/engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj"

fail() {
  echo >&2
  echo "CodeAtlas startup blocked: $1" >&2
  echo "No application process has been launched." >&2
  exit 1
}

run_step() {
  local label="$1"
  shift
  echo
  echo "================================================================"
  echo "[VERIFY] $label"
  echo "================================================================"
  "$@" || fail "$label failed (exit code $?)."
  echo "[PASS] $label"
}

[[ "$(uname -s)" == "Darwin" ]] || fail "The native macOS app must be built and launched on macOS."
command -v swift >/dev/null 2>&1 || fail "Swift was not found. Install Xcode / Command Line Tools."
command -v swiftc >/dev/null 2>&1 || fail "swiftc was not found. Install Xcode / Command Line Tools."
command -v dotnet >/dev/null 2>&1 || fail ".NET SDK 10 is required."
command -v node >/dev/null 2>&1 || fail "Node.js is required by the validation suite."
command -v git >/dev/null 2>&1 || fail "Git is required by the validation suite."
dotnet --list-sdks | grep -Eq '^10\.' || fail ".NET SDK 10.x was not found."

# Build the engine before tests so compiler errors cannot be hidden by the
# Swift shell successfully launching.
run_step ".NET engine build" dotnet build "$ENGINE_PROJECT" --configuration Debug --nologo

# This is the integration gate: scanner, flows, HTTP execution, process
# lifecycle, model compatibility and workspace behaviour must all pass.
run_step "Full engine and integration smoke suite" "$ROOT/scripts/smoke-test.sh"
run_step "Distributed flow regression tests" "$ROOT/scripts/distributed-flow-test.sh"
run_step "Nested Git workspace regression tests" "$ROOT/scripts/git-workspace-test.sh"
run_step "Clean Architecture grouping tests" "$ROOT/scripts/clean-architecture-grouper-test.sh"
run_step "Snapshot model compatibility tests" "$ROOT/scripts/model-compatibility-test.sh"
run_step "Runtime binding tests" "$ROOT/scripts/runtime-linker-test.sh"
run_step "Multi-repository snapshot merge tests" "$ROOT/scripts/workspace-merger-test.sh"
run_step "UI structure regression tests" "$ROOT/scripts/ui-structure-test.sh"
run_step "VS Code extension and local bridge tests" "$ROOT/scripts/vscode-extension-test.sh"
run_step "Code explorer tree regression tests" "$ROOT/scripts/code-file-tree-test.sh"
run_step "Code outline regression tests" "$ROOT/scripts/code-symbol-index-test.sh"
run_step "Language server protocol and client integration tests" "$ROOT/scripts/language-server-test.sh"
run_step "Roslyn code analysis, findings and fix tests" "$ROOT/scripts/code-analysis-test.sh"
run_step "Cancellable process execution tests" "$ROOT/scripts/process-execution-test.sh"
run_step "Compiled engine invocation resolver tests" "$ROOT/scripts/engine-invocation-test.sh"
run_step "Workspace cancellation and background-work regression tests" "$ROOT/scripts/workspace-cancellation-test.sh"
run_step "Vertical-slice structure regression tests" "$ROOT/scripts/vertical-slice-structure-test.sh"

# Compile the actual desktop target only after the logic/integration suite is green.
run_step "Native SwiftUI application build" swift build --package-path "$APP_DIR" --configuration debug

echo
echo "================================================================"
echo "ALL CHECKS PASSED — CodeAtlas is allowed to start."
echo "================================================================"
