#!/usr/bin/env bash
# Phase 2 gate: the Roslyn analyzer must report each seeded defect with evidence, stay silent on the clean
# counterparts, keep stable ids across line shifts, honor its cache/cancellation contract and never modify
# a file except through an explicit, hash-checked fix.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj"
FIXTURE="$ROOT/tests/fixtures/analysis/RiskyService"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/codeatlas-analysis.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
export CODEATLAS_CACHE_DIR="$WORK/cache"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

DOTNET_CLI_UI_LANGUAGE=en dotnet build "$PROJECT" --nologo --verbosity quiet >/dev/null
ENGINE="$(dirname "$PROJECT")/bin/Debug/net10.0/CodeAtlas.Engine.dll"
engine() { dotnet "$ENGINE" "$@"; }

REPO="$WORK/repo"
cp -R "$FIXTURE" "$REPO"
engine analyze "$REPO" > "$WORK/first.json"

node - "$WORK/first.json" <<'NODE'
const fs = require('node:fs');
const report = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const fail = (m, extra) => { console.error('Code analysis test failed: ' + m, extra ? JSON.stringify(extra, null, 2) : ''); process.exit(1); };
const at = (rule, member) => report.findings.filter(f => f.ruleId === rule && (f.location.symbol || '').includes('.' + member + '('));

if (report.schemaVersion !== 1 || report.cancelled) fail('unexpected report envelope', report.schemaVersion);
if (report.rules.length < 11) fail('rule catalog incomplete', report.rules.map(r => r.id));
if (report.warnings.length) fail('fixture should resolve fully (implicit usings) but produced warnings', report.warnings);

// 1. Seeded defects → exactly the expected rules, at the expected members, with the right evidence level.
const expectations = [
  ['ATL1001', 'BlockingResult', 'staticProven', 'info'],
  ['ATL1001', 'BlockingWait', 'staticProven', 'info'],
  ['ATL1001', 'BlockingAwaiter', 'staticProven', 'info'],
  ['ATL1001', 'BlockingInAsync', 'staticProven', 'warning'],
  ['ATL1002', 'FireAndForget', 'staticProven', 'warning'],
  ['ATL1003', 'NoToken', 'staticProven', 'warning'],
  ['ATL1004', 'EmptyCatch', 'staticProven', 'warning'],
  ['ATL1004', 'SilentCatch', 'staticRisk', 'info'],
  ['ATL1005', 'ThrowEx', 'staticProven', 'warning'],
  ['ATL1006', 'Leak', 'staticRisk', 'warning'],
  ['ATL1007', 'Loop', 'staticRisk', 'warning'],
  ['ATL1008', 'Twice', 'staticRisk', 'warning'],
  ['ATL1008', 'InLoop', 'staticRisk', 'warning'],
  ['ATL1009', 'Complex', 'staticProven', 'info'],
  ['ATL1010', 'Long', 'staticProven', 'info'],
];
for (const [rule, member, validation, severity] of expectations) {
  const hits = at(rule, member);
  if (hits.length !== 1) fail(`${rule} should report exactly one finding in ${member}, got ${hits.length}`, hits);
  if (hits[0].validation !== validation) fail(`${rule}/${member} must be ${validation} (never overstate evidence), got ${hits[0].validation}`);
  if (hits[0].severity !== severity) fail(`${rule}/${member} severity should be ${severity}, got ${hits[0].severity}`);
}
if (report.findings.length !== expectations.length) fail('unexpected extra findings (false positives?)',
  report.findings.filter(f => !expectations.some(([r, m]) => f.ruleId === r && (f.location.symbol || '').includes('.' + m + '('))).map(f => `${f.ruleId} ${f.location.file}:${f.location.line} ${f.title}`));

// 2. The clean counterparts produce nothing.
const clean = report.findings.filter(f => f.location.file === 'Clean.cs');
if (clean.length) fail('false positives in Clean.cs', clean.map(f => `${f.ruleId} ${f.location.line} ${f.title}`));

// 3. Every finding carries the full evidence contract.
const ids = new Set();
for (const f of report.findings) {
  if (!/^F-[0-9a-f]{12}$/.test(f.id)) fail('malformed id', f.id);
  if (ids.has(f.id)) fail('duplicate id', f.id);
  ids.add(f.id);
  if (!(f.location.line > 0 && f.location.file && f.location.symbol && f.location.project)) fail('imprecise location', f.location);
  if (!f.evidence.length || !f.evidence.some(e => e.description)) fail('finding without evidence', f.id);
  if (!f.recommendation || !f.reproduction || !f.impact || !Array.isArray(f.limitations)) fail('missing impact/recommendation/reproduction/limitations', f.id);
  if (f.origin !== 'static' || f.status !== 'new') fail('origin/status', f.id);
  if (f.validation === 'staticRisk' && f.limitations.length === 0) fail('a static risk must state its limitations', f.id);
  if (!f.history?.length) fail('history missing', f.id);
}

// 4. Evidence details that matter to the user.
const loop = at('ATL1007', 'Loop')[0];
if (!loop.evidence.some(e => e.kind === 'loop' && e.location?.line > 0)) fail('ATL1007 must point at the loop', loop.evidence);
const twice = at('ATL1008', 'Twice')[0];
if (twice.evidence.filter(e => e.kind === 'enumeration').length !== 2) fail('ATL1008 must list both enumeration sites', twice.evidence);
if (!at('ATL1003', 'NoToken')[0].fix?.unifiedDiff.includes('Delay(10, ct)')) fail('ATL1003 fix must add the token');
if (!at('ATL1005', 'ThrowEx')[0].fix?.unifiedDiff.includes('+') || !at('ATL1005', 'ThrowEx')[0].fix.unifiedDiff.includes('throw;')) fail('ATL1005 fix must produce throw;');

// 5. Telemetry is present and honest.
const t = report.telemetry;
if (!(t.totalMilliseconds > 0 && t.documentsAnalyzed === 2 && t.rules.length === report.rules.length)) fail('telemetry incomplete', t);
console.log(`Static findings verified: ${report.findings.length} seeded defects reported, Clean.cs silent, ${report.rules.length} rules.`);
NODE

# 6. Stable ids: pure line shifts must not change finding identity.
{ printf '// shifted\n// shifted\n// shifted\n'; cat "$REPO/Defects.cs"; } > "$WORK/Defects.shifted" && mv "$WORK/Defects.shifted" "$REPO/Defects.cs"
engine analyze "$REPO" > "$WORK/shifted.json"
node - "$WORK/first.json" "$WORK/shifted.json" <<'NODE'
const fs = require('node:fs');
const [a, b] = process.argv.slice(2).map(p => JSON.parse(fs.readFileSync(p, 'utf8')));
const idsA = a.findings.map(f => f.id).sort(), idsB = b.findings.map(f => f.id).sort();
if (JSON.stringify(idsA) !== JSON.stringify(idsB)) { console.error('Stable-id test failed: ids changed after a pure line shift', idsA.filter(i => !idsB.includes(i))); process.exit(1); }
const line = (r) => r.findings.find(f => f.ruleId === 'ATL1005').location.line;
if (line(b) !== line(a) + 3) { console.error('Stable-id test failed: locations did not follow the shift'); process.exit(1); }
if (b.telemetry.projectsFromCache !== 0) { console.error('Cache must be invalidated by an edit.'); process.exit(1); }
console.log('Stable ids verified: 3-line shift kept all ids, moved locations, and invalidated the cache.');
NODE

# 7. Cache: an unchanged project is served from the cache and returns identical findings.
engine analyze "$REPO" > "$WORK/cached.json"
engine analyze "$REPO" --no-cache > "$WORK/nocache.json"
node - "$WORK/shifted.json" "$WORK/cached.json" "$WORK/nocache.json" <<'NODE'
const fs = require('node:fs');
const [fresh, cached, nocache] = process.argv.slice(2).map(p => JSON.parse(fs.readFileSync(p, 'utf8')));
if (cached.telemetry.projectsFromCache !== 1 || !cached.projects[0].fromCache) { console.error('Cache test failed: unchanged project was re-analyzed.', cached.telemetry); process.exit(1); }
if (nocache.telemetry.projectsFromCache !== 0) { console.error('Cache test failed: --no-cache used the cache.'); process.exit(1); }
const key = r => JSON.stringify(r.findings.map(f => [f.id, f.ruleId, f.location.line, f.validation]));
if (key(cached) !== key(fresh) || key(nocache) !== key(fresh)) { console.error('Cache test failed: cached findings differ from fresh findings.'); process.exit(1); }
console.log('Cache verified: unchanged project served from cache with identical findings; --no-cache bypasses it.');
NODE

# 8. Rule filter and configuration (severity override + disabled rule).
engine analyze "$REPO" --no-cache --rules ATL1005,NOPE > "$WORK/filtered.json"
cat > "$REPO/codeatlas.analysis.json" <<'JSON'
{ "exclude": ["**/Clean.cs"], "rules": { "ATL1005": { "severity": "error" }, "ATL1002": { "enabled": false }, "ATL1009": { "threshold": 5 }, "ATL1010": { "threshold": 10 } } }
JSON
engine analyze "$REPO" --no-cache > "$WORK/configured.json"
node - "$WORK/filtered.json" "$WORK/configured.json" <<'NODE'
const fs = require('node:fs');
const [filtered, configured] = process.argv.slice(2).map(p => JSON.parse(fs.readFileSync(p, 'utf8')));
const fail = m => { console.error('Configuration test failed: ' + m); process.exit(1); };
if (filtered.findings.length !== 1 || filtered.findings[0].ruleId !== 'ATL1005') fail('--rules did not restrict the run');
if (!filtered.warnings.some(w => w.includes('NOPE'))) fail('unknown rule id was not reported');
if (configured.findings.some(f => f.ruleId === 'ATL1002')) fail('disabled rule still reported');
if (configured.findings.find(f => f.ruleId === 'ATL1005')?.severity !== 'error') fail('severity override ignored');
console.log('Configuration verified: rule filter, unknown-rule warning, disabled rule and severity override.');
NODE
cp "$FIXTURE/codeatlas.analysis.json" "$REPO/codeatlas.analysis.json"

# 9. Syntax errors are proven findings even without restored packages.
printf 'namespace RiskyService;\npublic class Broken {\n    void M( { }\n}\n' > "$REPO/Broken.cs"
engine analyze "$REPO" --no-cache > "$WORK/broken.json"
node -e '
const r = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
const e = r.findings.filter(f => f.ruleId === "ATL0001" && f.location.file === "Broken.cs");
if (!e.length || e[0].severity !== "error" || e[0].validation !== "staticProven") { console.error("Syntax-error test failed", e); process.exit(1); }
console.log("Syntax error verified: ATL0001 reported as proven error.");' "$WORK/broken.json"
rm "$REPO/Broken.cs"

# 10. Fixes: the diff is shown first, nothing changes without --apply, a stale preview is refused, an applied fix works.
FIX_ID="$(engine analyze "$REPO" --no-cache | node -e 'const r=JSON.parse(require("fs").readFileSync(0,"utf8")); console.log(r.findings.find(f=>f.ruleId==="ATL1005").id)')"
BEFORE="$(shasum -a 256 "$REPO/Defects.cs" | cut -d' ' -f1)"
PREVIEW="$(engine fix "$REPO" "$FIX_ID")"
echo "$PREVIEW" | grep -q -- '-        catch (FormatException ex) { throw ex; }' || { echo "Fix test failed: preview lacks the removed line." >&2; echo "$PREVIEW" >&2; exit 1; }
echo "$PREVIEW" | grep -q -- '+        catch (FormatException ex) { throw; }' || { echo "Fix test failed: preview lacks the added line." >&2; exit 1; }
[[ "$(shasum -a 256 "$REPO/Defects.cs" | cut -d' ' -f1)" == "$BEFORE" ]] || { echo "Fix test failed: preview modified the file." >&2; exit 1; }
if engine fix "$REPO" "$FIX_ID" --apply --base-hash 0000 >/dev/null 2>&1; then echo "Fix test failed: a stale base hash was accepted." >&2; exit 1; fi
[[ "$(shasum -a 256 "$REPO/Defects.cs" | cut -d' ' -f1)" == "$BEFORE" ]] || { echo "Fix test failed: stale apply modified the file." >&2; exit 1; }
engine fix "$REPO" "$FIX_ID" --apply >/dev/null 2>&1
grep -q 'catch (FormatException ex) { throw; }' "$REPO/Defects.cs" || { echo "Fix test failed: fix was not applied." >&2; exit 1; }
if engine analyze "$REPO" --no-cache | node -e 'const r=JSON.parse(require("fs").readFileSync(0,"utf8")); process.exit(r.findings.some(f=>f.ruleId==="ATL1005")?1:0)'; then
  echo "Fixes verified: preview first, stale hash refused, applied fix removes the finding."
else
  echo "Fix test failed: finding remains after applying its fix." >&2; exit 1
fi

# 11. Cancellation: SIGTERM mid-run stops the analysis and yields a partial report that says so.
# 150 projects take ~10 s here, so a signal at 3 s must land mid-run; finishing first means the workload is too small.
BIG="$WORK/big"; mkdir -p "$BIG"
for i in $(seq 1 150); do mkdir -p "$BIG/P$i"; cp "$FIXTURE/RiskyService.csproj" "$BIG/P$i/P$i.csproj"; cp "$FIXTURE/Defects.cs" "$BIG/P$i/Defects.cs"; done
dotnet "$ENGINE" analyze "$BIG" --no-cache > "$WORK/cancel.json" &
PID=$!
sleep 3
kill -TERM "$PID"
CODE=0; wait "$PID" || CODE=$?
[[ "$CODE" == "130" ]] || { echo "Cancellation test failed: expected exit 130 after SIGTERM, got $CODE (0 means the run finished before the signal)." >&2; exit 1; }
node -e '
const r = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
if (!r.cancelled || !r.warnings.some(w => w.includes("cancelled"))) { console.error("Cancellation test failed: report not marked cancelled"); process.exit(1); }
if (r.projects.length === 0 || r.projects.length >= 150) { console.error("Cancellation test failed: expected a partial result, got " + r.projects.length + " projects"); process.exit(1); }
console.log("Cancellation verified: SIGTERM mid-run produced a partial report (" + r.projects.length + "/150 projects) marked cancelled, exit 130.");' "$WORK/cancel.json"

echo "Code analysis test passed."
