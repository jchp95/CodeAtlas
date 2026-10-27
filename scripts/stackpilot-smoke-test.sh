#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj"
CONFIG="$(mktemp "${TMPDIR:-/tmp}/codeatlas-stackpilot-config.XXXXXX")"
RESULT="$(mktemp "${TMPDIR:-/tmp}/codeatlas-stackpilot-result.XXXXXX")"
PROFILE="stackpilot-smoke-$$"
STARTED=0

cleanup() {
  if [[ "$STARTED" == "1" ]]; then
    dotnet run --verbosity quiet --project "$PROJECT" -- stackpilot stop "$PROFILE" sleep-check --config "$CONFIG" >/dev/null 2>&1 || true
  fi
  rm -f "$CONFIG" "$RESULT"
}
trap cleanup EXIT

node - "$CONFIG" "$ROOT" "$PROFILE" <<'NODE'
const fs = require('node:fs');
const [path, root, profile] = process.argv.slice(2);
const config = {
  version: 1,
  profiles: [{
    id: profile,
    name: 'StackPilot smoke test',
    workspaceRoot: root,
    services: [{
      id: 'sleep-check', name: 'Managed sleep process', enabled: true,
      kind: 'process', command: '/bin/sleep', arguments: ['120'],
      workingDirectory: '.', dependsOn: [], environment: {}
    }]
  }]
};
fs.writeFileSync(path, JSON.stringify(config, null, 2));
NODE

run_stackpilot() {
  dotnet run --verbosity quiet --project "$PROJECT" -- stackpilot "$@" > "$RESULT"
}

run_stackpilot status --config "$CONFIG"
node - "$RESULT" "$PROFILE" <<'NODE'
const fs = require('node:fs');
const result = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const profile = result.profiles.find(p => p.id === process.argv[3]);
if (result.error || !result.configured || !profile || profile.services[0]?.status !== 'stopped') {
  console.error('Initial StackPilot status failed:', result.error || JSON.stringify(result)); process.exit(1);
}
NODE

run_stackpilot start "$PROFILE" sleep-check --config "$CONFIG"
STARTED=1
node - "$RESULT" "$PROFILE" <<'NODE'
const fs = require('node:fs');
const result = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const profile = result.profiles.find(p => p.id === process.argv[3]);
const service = profile?.services.find(s => s.id === 'sleep-check');
if (result.error || service?.status !== 'running' || !service.pid) {
  console.error('StackPilot process start failed:', result.error || JSON.stringify(result)); process.exit(1);
}
NODE

run_stackpilot stop "$PROFILE" sleep-check --config "$CONFIG"
node - "$RESULT" "$PROFILE" <<'NODE'
const fs = require('node:fs');
const result = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const profile = result.profiles.find(p => p.id === process.argv[3]);
const service = profile?.services.find(s => s.id === 'sleep-check');
if (result.error || service?.status !== 'stopped') {
  console.error('StackPilot process stop failed:', result.error || JSON.stringify(result)); process.exit(1);
}
console.log('StackPilot smoke test passed: status, managed start, PID tracking, and stop.');
NODE
STARTED=0
