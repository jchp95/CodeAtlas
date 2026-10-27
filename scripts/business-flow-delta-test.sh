#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FIXTURE="$ROOT/tests/fixtures/architecture"
PROJECT="$ROOT/engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj"
TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/codeatlas-flow-delta.XXXXXX")"
BASELINE="$TEMP_DIR/baseline.json"
CURRENT="$TEMP_DIR/current.json"
DELTA="$TEMP_DIR/delta.json"
trap 'rm -rf "$TEMP_DIR"' EXIT

cp -R "$FIXTURE" "$TEMP_DIR/fixture"

scan() {
  DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --verbosity quiet --project "$PROJECT" -- scan "$TEMP_DIR/fixture" --no-persist
}

scan > "$BASELINE"
node - "$TEMP_DIR/fixture/zas-express-api/Handlers/RegisterCargoCommandHandler.cs" <<'NODE'
const fs = require('node:fs');
const file = process.argv[2];
const original = fs.readFileSync(file, 'utf8');
const before = 'new CargoRegistered(request.CargoId)';
if (!original.includes(before)) {
  console.error('Flow-delta fixture did not contain the expected publish expression.');
  process.exit(1);
}
fs.writeFileSync(file, original.replace(before, 'new CargoDispatched(request.CargoId)'));
NODE
scan > "$CURRENT"
DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --verbosity quiet --project "$PROJECT" -- compare "$BASELINE" "$CURRENT" > "$DELTA"
node - "$DELTA" <<'NODE'
const fs = require('node:fs');
const delta = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const changes = Array.isArray(delta.flowChanges) ? delta.flowChanges : [];
const modified = changes.find(change => change.status === 'modified' && change.name === 'POST /api/cargo');
if (!modified) {
  console.error('Expected POST /api/cargo flow to be marked modified:', JSON.stringify(changes));
  process.exit(1);
}
if (!modified.previousSteps.some(step => step.symbol.includes('CargoRegistered')) ||
    !modified.currentSteps.some(step => step.symbol.includes('CargoDispatched'))) {
  console.error('Flow delta did not preserve before/after message evidence:', JSON.stringify(modified));
  process.exit(1);
}
const distributedChanges = Array.isArray(delta.distributedFlowChanges) ? delta.distributedFlowChanges : [];
const removedDistributed = distributedChanges.find(change => change.status === 'removed'
  && change.contract === 'Zas.Contracts.CargoRegistered'
  && change.producerProjectName === 'ZasExpress.Api' && change.consumerProjectName === 'ZasTrack.Api');
if (!removedDistributed) {
  console.error('Expected distributed flow to be removed when the producer stops publishing the consumer contract:', JSON.stringify(distributedChanges));
  process.exit(1);
}
console.log('Business-flow delta test passed: changed contract is shown in the local before/after path and breaks the distributed route.');
NODE
