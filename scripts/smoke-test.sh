#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FIXTURE="$ROOT/tests/fixtures/architecture"
TS_FIXTURE="$ROOT/tests/fixtures/typescript"
"$ROOT/scripts/runtime-linker-test.sh"
"$ROOT/scripts/model-compatibility-test.sh"
"$ROOT/scripts/clean-architecture-grouper-test.sh"
"$ROOT/scripts/workspace-merger-test.sh"
"$ROOT/scripts/git-workspace-test.sh"
OUTPUT="$(mktemp "${TMPDIR:-/tmp}/codeatlas-scan.XXXXXX")"
TS_OUTPUT="$(mktemp "${TMPDIR:-/tmp}/codeatlas-typescript.XXXXXX")"
trap 'rm -f "$OUTPUT" "$TS_OUTPUT"' EXIT

node --check "$ROOT/engine/CodeAtlas.Engine/tools/typescript-analyzer.cjs"
node "$ROOT/engine/CodeAtlas.Engine/tools/typescript-analyzer.cjs" "$TS_FIXTURE" > "$TS_OUTPUT"
node - "$TS_OUTPUT" <<'NODE'
const fs = require('node:fs');
const result = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
if (typeof result.available !== 'boolean' || !Array.isArray(result.relations) || !Array.isArray(result.diagnostics)) {
  console.error('TypeScript analyzer returned an invalid response shape.');
  process.exit(1);
}
if (result.available) {
  if (result.analyzedProjects < 1) {
    console.error('TypeScript compiler resolved but no fixture tsconfig was analyzed.'); process.exit(1);
  }
  const expected = result.relations.some(r =>
    r.sourceProject === 'packages/operations-ui/package.json' &&
    r.targetProject === 'packages/tracking-client/package.json' &&
    (r.evidence || '').includes('packages/operations-ui/src/tracking.ts'));
  if (!expected) {
    console.error('TypeScript compiler ran but did not resolve the expected cross-package fixture import:', JSON.stringify(result.relations));
    process.exit(1);
  }
  console.log(`TypeScript analyzer passed: ${result.analyzedProjects} tsconfig project(s), ${result.relations.length} cross-package relation(s).`);
} else {
  console.log('TypeScript analyzer SKIPPED: fixture is valid, but no local or discoverable global TypeScript compiler is installed; only the response shape was validated.');
}
NODE

DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --verbosity quiet --project "$ROOT/engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj" -- scan "$FIXTURE" --no-persist > "$OUTPUT"
node - "$OUTPUT" <<'NODE'
const fs = require('node:fs');
const snapshot = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const types = new Set(snapshot.relations.map((r) => r.type));
const flows = Array.isArray(snapshot.businessFlows) ? snapshot.businessFlows : [];
const failures = [];
if (snapshot.projects.length < 3) failures.push(`expected >= 3 projects, got ${snapshot.projects.length}`);
if (!types.has('message-flow')) failures.push('missing message-flow relation');
if (!types.has('http-call')) failures.push('missing http-call relation');
if (!snapshot.projects.some((p) => p.kind === 'Next.js')) failures.push('missing Next.js project');
const infrastructure = snapshot.projects.filter((p) => p.kind === 'Infrastructure');
if (!infrastructure.some((p) => p.name === 'RabbitMQ')) failures.push('missing RabbitMQ infrastructure block');
if (!infrastructure.some((p) => p.name === 'MinIO')) failures.push('missing MinIO infrastructure block');
const expressProject = snapshot.projects.find((p) => p.name === 'ZasExpress.Api');
if (expressProject && !snapshot.relations.some((r) => r.type === 'infrastructure' && r.sourceId === expressProject.id && r.targetId === 'infrastructure:rabbitmq')) failures.push('missing inferred ZasExpress.Api -> RabbitMQ relation');
if (expressProject && !snapshot.relations.some((r) => r.type === 'infrastructure' && r.sourceId === expressProject.id && r.targetId === 'infrastructure:minio')) failures.push('missing inferred ZasExpress.Api -> MinIO relation');
if (!snapshot.projects.find((p) => p.kind === 'Dotnet')?.endpoints.some((e) => e.includes('/api/health'))) failures.push('missing Roslyn-parsed minimal API endpoint');
const cargoFlow = flows.find((f) => f.projectName === 'ZasExpress.Api' && f.entryPoint === 'POST /api/cargo');
if (!cargoFlow) failures.push('missing POST /api/cargo business-flow trace');
if (cargoFlow && !cargoFlow.steps.some((step) => step.kind === 'request-handler' && step.symbol.includes('RegisterCargoCommandHandler'))) failures.push('business flow did not resolve RegisterCargoCommandHandler');
if (cargoFlow && !cargoFlow.steps.some((step) => step.kind === 'service-implementation' && step.symbol.includes('TrackingClient.RegisterAsync'))) failures.push('business flow did not follow DI registration to TrackingClient.RegisterAsync');
if (cargoFlow && !cargoFlow.steps.some((step) => step.kind === 'message-publish' && step.symbol.includes('Zas.Contracts.CargoRegistered'))) failures.push('business flow did not include the CargoRegistered publish step');
const express = snapshot.projects.find((p) => p.name === 'ZasExpress.Api');
const track = snapshot.projects.find((p) => p.name === 'ZasTrack.Api');
const messageContract = 'Zas.Contracts.CargoRegistered';
if (!express?.contractsPublished.includes(messageContract)) failures.push('publisher contract was not semantically resolved to Zas.Contracts.CargoRegistered');
if (!track?.contractsConsumed.includes(messageContract)) failures.push('consumer contract was not semantically resolved to Zas.Contracts.CargoRegistered');
if (!snapshot.relations.some((r) => r.type === 'message-flow' && r.evidence.includes(messageContract))) failures.push('message-flow evidence is missing the fully-qualified shared contract');
const distributedFlows = Array.isArray(snapshot.distributedFlows) ? snapshot.distributedFlows : [];
const distributedFlow = distributedFlows.find(flow => flow.contract === messageContract
  && flow.producerProjectName === 'ZasExpress.Api' && flow.consumerProjectName === 'ZasTrack.Api');
if (!distributedFlow) failures.push('missing distributed producer-to-consumer flow for CargoRegistered');
if (distributedFlow) {
  const phases = new Set(distributedFlow.steps.map(step => step.phase));
  if (!phases.has('producer') || !phases.has('transport') || !phases.has('consumer')) failures.push('distributed flow is missing a producer, transport, or consumer phase');
  if (!distributedFlow.steps.some(step => step.phase === 'consumer' && step.kind === 'message-consumer')) failures.push('distributed flow does not include the IConsumer<T> entry point');
  if (!distributedFlow.steps.some(step => step.phase === 'consumer' && step.symbol.includes('CargoProjection.ApplyAsync'))) failures.push('distributed flow did not trace from the MassTransit consumer into CargoProjection.ApplyAsync');
  if (!distributedFlow.steps.some(step => step.phase === 'producer' && step.symbol.includes('POST /api/cargo'))) failures.push('distributed flow lost the originating POST /api/cargo path');
}
if (!express?.codeEvidence?.some((e) => e.category === 'service-registration' && e.symbol.endsWith('ITrackingClient'))) failures.push('missing generic DI service-registration evidence');
if (!express?.codeEvidence?.some((e) => e.category === 'constructor-injection' && e.symbol.endsWith('ITrackingClient') && e.detail.includes('Constructor primario'))) failures.push('missing C# 12 primary-constructor injection evidence for ITrackingClient');
if (failures.length) {
  console.error('Smoke test failed:\n- ' + failures.join('\n- '));
  if (failures.some(failure => failure.includes('distributed flow did not trace') || failure.includes('IConsumer<T>'))) {
    console.error('Distributed flow trace:', JSON.stringify(distributedFlow ?? null, null, 2));
    console.error('Consumer business-flow candidates:', JSON.stringify(flows.filter(flow => flow.projectName === 'ZasTrack.Api' && flow.entryPoint.startsWith('CONSUME ')), null, 2));
  }
  process.exit(1);
}
console.log(`Smoke test passed: ${snapshot.projects.length} projects, ${snapshot.relations.length} relations, ${flows.length} business flows, ${distributedFlows.length} distributed flows.`);
NODE

"$ROOT/scripts/business-flow-delta-test.sh"
"$ROOT/scripts/business-flow-source-index-test.sh"

# StackPilot process lifecycle: starts only an explicitly configured fixture process.
"$ROOT/scripts/stackpilot-smoke-test.sh"

"$ROOT/scripts/http-catalog-test.sh"
"$ROOT/scripts/http-request-execution-test.sh"
