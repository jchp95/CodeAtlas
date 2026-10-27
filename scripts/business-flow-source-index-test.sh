#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FIXTURE="$ROOT/tests/fixtures/business-flow-source-index"
PROJECT="$ROOT/engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj"
OUTPUT="$(mktemp "${TMPDIR:-/tmp}/codeatlas-source-index.XXXXXX")"
trap 'rm -f "$OUTPUT"' EXIT

DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --verbosity quiet --project "$PROJECT" -- scan "$FIXTURE" --no-persist > "$OUTPUT"
node - "$OUTPUT" <<'NODE'
const fs = require('node:fs');
const snapshot = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const flows = Array.isArray(snapshot.businessFlows) ? snapshot.businessFlows : [];
const flow = flows.find(item => item.projectName === 'SharedFlow.Api' && item.entryPoint === 'POST /api/cargo');
if (!flow) {
  console.error('Source-index test failed: POST /api/cargo flow was not detected.');
  process.exit(1);
}
const publish = flow.steps.find(step => step.kind === 'message-publish');
if (!publish || !publish.symbol.includes('Shared.Contracts.CargoRegistered')) {
  console.error('Source-index test failed: unresolved project-reference contract was not namespace-qualified:', JSON.stringify(flow.steps));
  process.exit(1);
}
console.log('Business-flow source-index test passed: cross-project event contract retained its namespace without building ProjectReference outputs.');
NODE
