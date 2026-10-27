#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FIXTURE="$ROOT/tests/fixtures/architecture"
PROJECT="$ROOT/engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj"
OUTPUT="$(mktemp "${TMPDIR:-/tmp}/codeatlas-distributed-flow.XXXXXX")"
trap 'rm -f "$OUTPUT"' EXIT

DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --verbosity quiet --project "$PROJECT" -- scan "$FIXTURE" --no-persist > "$OUTPUT"
node - "$OUTPUT" <<'NODE'
const fs = require('node:fs');
const snapshot = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const target = (snapshot.distributedFlows || []).find(flow =>
  flow.contract === 'Zas.Contracts.CargoRegistered' &&
  flow.producerProjectName === 'ZasExpress.Api' &&
  flow.consumerProjectName === 'ZasTrack.Api');
if (!target) {
  console.error('Distributed-flow test failed: no CargoRegistered path links ZasExpress.Api to ZasTrack.Api.');
  process.exit(1);
}
for (const phase of ['producer', 'transport', 'consumer']) {
  if (!target.steps.some(step => step.phase === phase)) {
    console.error(`Distributed-flow test failed: missing ${phase} phase.`, JSON.stringify(target.steps));
    process.exit(1);
  }
}
if (!target.steps.some(step => step.phase === 'producer' && step.symbol.includes('POST /api/cargo'))) {
  console.error('Distributed-flow test failed: originating endpoint is not included.');
  process.exit(1);
}
if (!target.steps.some(step => step.phase === 'consumer' && step.kind === 'message-consumer')) {
  console.error('Distributed-flow test failed: consumer entry point is not included.');
  process.exit(1);
}
if (!target.steps.some(step => step.phase === 'consumer' && step.symbol.includes('CargoProjection.ApplyAsync'))) {
  console.error('Distributed-flow test failed: consumer business logic was not traced.', JSON.stringify(target.steps.filter(step => step.phase === 'consumer'), null, 2));
  process.exit(1);
}
console.log(`Distributed-flow test passed: ${target.producerProjectName} → ${target.contract} → ${target.consumerProjectName}, ${target.steps.length} source-backed steps.`);
NODE
