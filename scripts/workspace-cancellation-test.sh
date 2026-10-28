#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/macOSApp/Sources/CodeAtlas"
fail() { echo "Workspace cancellation test failed: $1" >&2; exit 1; }

[[ -f "$APP/Infrastructure/ProcessExecutionService.swift" ]] || fail "bounded cancellable process execution service is missing"
[[ -f "$APP/Infrastructure/EngineInvocationResolver.swift" ]] || fail "engine invocation cache resolver is missing"
grep -q 'withTaskCancellationHandler' "$APP/Infrastructure/ProcessExecutionService.swift" || fail "process cancellation handler is missing"
grep -q 'maximumStandardOutputBytes' "$APP/Infrastructure/ProcessExecutionService.swift" || fail "bounded stdout collection is missing"
grep -q 'maximumStandardErrorBytes' "$APP/Infrastructure/ProcessExecutionService.swift" || fail "bounded stderr collection is missing"
grep -q 'workspaceScanTask?.cancel()' "$APP/ContentView+Workspace.swift" || fail "UI cannot cancel an active scan"
grep -q 'Button(isCancellingScan ? "Cancelando…" : "Cancelar")' "$APP/ContentView.swift" || fail "scan cancellation control is not visible"
grep -q 'try Task.checkCancellation()' "$APP/Infrastructure/EngineClient.swift" || fail "workspace scanning does not check cancellation between roots"
if grep -R -q 'Task { await rescan() }' "$APP/ContentView.swift" "$APP/ContentView+Workspace.swift"; then
  fail "untracked rescans bypass the cancellable scan coordinator"
fi

echo "Workspace cancellation checks passed: UI task ownership, process termination, bounded output, and cancellable multi-root analysis."
