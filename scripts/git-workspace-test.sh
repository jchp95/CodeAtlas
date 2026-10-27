#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ENGINE_PROJECT="$ROOT/engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj"
TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/codeatlas-git-workspace.XXXXXX")"
trap 'rm -rf "$TEMP_DIR"' EXIT
WORKSPACE="$TEMP_DIR/workspace"
mkdir -p "$WORKSPACE/service-a" "$WORKSPACE/service-b"

init_repo() {
  local path="$1"
  git -C "$path" init -q
  git -C "$path" config user.name "CodeAtlas Test"
  git -C "$path" config user.email "codeatlas-test@example.invalid"
  printf 'initial\n' > "$path/README.md"
  git -C "$path" add README.md
  git -C "$path" commit -qm 'initial fixture commit'
}

init_repo "$WORKSPACE/service-a"
mkdir -p "$WORKSPACE/service-a/nested-service"
init_repo "$WORKSPACE/service-a/nested-service"
init_repo "$WORKSPACE/service-b"

run_engine() {
  DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --verbosity quiet --project "$ENGINE_PROJECT" -- "$@"
}

BASE_FINGERPRINT="$(run_engine fingerprint "$WORKSPACE")"
printf 'alpha\n' > "$WORKSPACE/service-a/README.md"
FIRST_DIRTY_FINGERPRINT="$(run_engine fingerprint "$WORKSPACE")"
if [[ "$BASE_FINGERPRINT" == "$FIRST_DIRTY_FINGERPRINT" ]]; then
  echo "Git workspace fingerprint did not change after editing a tracked file." >&2
  exit 1
fi
# Keep the path and Git status identical while changing content; this catches path-only fingerprints.
printf 'bravo\n' > "$WORKSPACE/service-a/README.md"
SECOND_DIRTY_FINGERPRINT="$(run_engine fingerprint "$WORKSPACE")"
if [[ "$FIRST_DIRTY_FINGERPRINT" == "$SECOND_DIRTY_FINGERPRINT" ]]; then
  echo "Git workspace fingerprint ignored content changes to an already-dirty file." >&2
  exit 1
fi

SNAPSHOT="$TEMP_DIR/snapshot.json"
run_engine scan "$WORKSPACE" --no-persist > "$SNAPSHOT"
node - "$SNAPSHOT" <<'NODE'
const fs = require('node:fs');
const snapshot = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const repos = Array.isArray(snapshot.gitRepositories) ? snapshot.gitRepositories : [];
const paths = repos.map(repo => repo.path).sort();
if (JSON.stringify(paths) !== JSON.stringify(['service-a', 'service-a/nested-service', 'service-b'])) {
  console.error('Expected both nested Git repos; got:', JSON.stringify(paths)); process.exit(1);
}
const serviceA = repos.find(repo => repo.path === 'service-a');
if (!serviceA?.changes?.some(change => change.path === 'service-a/README.md')) {
  console.error('Expected workspace-relative dirty path for service-a; got:', JSON.stringify(serviceA?.changes)); process.exit(1);
}
if (repos.some(repo => !repo.branch || !repo.headCommit || repo.headCommit === '(sin commit)')) {
  console.error('Expected each repository to expose branch and HEAD:', JSON.stringify(repos)); process.exit(1);
}
if (!snapshot.branch.startsWith('workspace (3 repos)')) {
  console.error('Expected workspace branch summary; got:', snapshot.branch); process.exit(1);
}
if (snapshot.changes.some(change => change.path.includes('service-a/nested-service'))) {
  console.error('Parent repository reported the nested repository as a duplicate change:', JSON.stringify(snapshot.changes)); process.exit(1);
}
console.log('Git workspace test passed: nested repositories, workspace-relative changes, duplicate suppression, and dirty-file content fingerprints.');
NODE
