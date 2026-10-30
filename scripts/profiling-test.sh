#!/usr/bin/env bash
# Phase 5 gate: real profiles (dotnet-trace, dotnet-counters, dotnet-gcdump) of workloads whose shape is known, checked for what they must
# find AND for what they must not claim (allocation is not retention, one run is not a verdict, no tool = explicit error, no consent = no run).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj"
FIX="$ROOT/tests/fixtures/profiling/Busy"
WORK="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/codeatlas-prof.XXXXXX")" && pwd -P)"
trap 'pkill -f "$WORK/out/Busy.dll" 2>/dev/null || true; rm -rf "$WORK"' EXIT
export CODEATLAS_CACHE_DIR="$WORK/cache" CODEATLAS_DATA_DIR="$WORK/data"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

dotnet build "$PROJECT" --nologo --verbosity quiet >/dev/null
ENGINE="$(dirname "$PROJECT")/bin/Debug/net10.0/CodeAtlas.Engine.dll"
engine() { dotnet "$ENGINE" "$@"; }
fail() { echo "Profiling test failed: $1" >&2; exit 1; }

# --- always: missing tools and missing consent are explicit
mkdir -p "$WORK/emptyhome" "$WORK/repo"
CODE=0; HOME="$WORK/emptyhome" PATH="/usr/bin:/bin:$(dirname "$(command -v dotnet)")" CODEATLAS_DOTNET_TRACE="" dotnet "$ENGINE" runtime doctor > "$WORK/doctor-missing.json" || CODE=$?
[[ "$CODE" == "5" ]] && grep -q '"toolsMissing"' "$WORK/doctor-missing.json" || fail "runtime doctor without tools must exit 5 with toolsMissing"
cp -R "$FIX" "$WORK/repo/Busy"
for kind in cpu counters heap; do
  CODE=0; engine profile $kind "$WORK/repo" --program "$WORK/out/Busy.dll" > "$WORK/auth-$kind.json" || CODE=$?
  [[ "$CODE" == "7" ]] || fail "profile $kind without --allow-execution must exit 7, got $CODE"
done
CODE=0; engine profile cpu "$WORK/repo" --attach 1 > "$WORK/auth-attach.json" || CODE=$?
[[ "$CODE" == "7" ]] || fail "attach without --allow-attach must exit 7"
[[ ! -d "$WORK/data/profiles" ]] || fail "a refused profile must leave no output"
echo "Tools and consent verified: missing tools exit 5 with install instructions; execution and attach need explicit flags (exit 7, nothing written)."

engine runtime doctor > "$WORK/doctor.json" || true
if ! grep -q '"ready"' "$WORK/doctor.json"; then echo "SKIPPED: dotnet-trace/dotnet-counters/dotnet-gcdump are not installed (see 'runtime doctor'); live profiling tests did not run."; exit 0; fi

(cd "$WORK/repo/Busy" && dotnet build -c Release -o "$WORK/out" --nologo -v q >/dev/null)
APP="$WORK/out/Busy.dll"
cat > "$WORK/lib.cjs" <<'JS'
const fs = require('node:fs');
exports.load = p => JSON.parse(fs.readFileSync(p, 'utf8'));
exports.fail = m => { console.error('Profiling test failed: ' + m); process.exit(1); };
JS

# --- CPU: three real traces of a time-bound CPU workload
engine profile cpu "$WORK/repo" --program "$APP" --args "cpu 5" --allow-execution --repeat 3 > "$WORK/cpu.json"
node - "$WORK/cpu.json" "$WORK" <<'NODE'
const { load, fail } = require(process.argv[3] + '/lib.cjs');
const r = load(process.argv[2]);
if (r.status !== 'completed' || r.runs.length !== 3 || r.runs.some(x => x.status !== 'completed' || x.cpuMs < 2000)) fail('three completed traces with CPU time expected: ' + JSON.stringify(r.runs));
const hot = r.methods[0];
if (!hot.symbol.includes('Busy.Workloads.HotLoop') || hot.resolution !== 'resolved' || !hot.nodeId || hot.location.file !== 'Busy/Program.cs') fail('the hottest method should be HotLoop resolved to the call graph: ' + JSON.stringify(hot).slice(0, 300));
if (!(hot.ownShare > 0.6 && hot.ownShare <= 1) || hot.runsPresent !== 3 || !(hot.minOwnShare <= hot.ownShare && hot.ownShare <= hot.maxOwnShare)) fail('HotLoop share and its range over the runs: ' + JSON.stringify(hot).slice(0, 300));
const total = r.methods.filter(m => m.nodeId).reduce((a, m) => a + m.ownShare, 0);
if (total > 1.02) fail('own shares of source methods must not exceed 100 % (' + total + ')');
const cheap = r.methods.find(m => m.symbol.includes('Workloads.Cheap'));
if (cheap && cheap.ownShare >= hot.ownShare) fail('Cheap() must not outweigh HotLoop');
const f = r.findings.filter(x => x.ruleId === 'PRF0001');
if (f.length < 1 || f[0].origin !== 'profile' || f[0].validation !== 'measured' || !f[0].title.includes('HotLoop') || f.some(x => x.title.includes('Main('))) fail('PRF0001 should name HotLoop (and never the entry point)');
if (!f[0].recommendation.includes('BenchmarkDotNet') || !f[0].limitations.some(l => l.includes('not necessarily'))) fail('a hot spot must not be presented as a defect');
if (r.callTree.children.length === 0 || !JSON.stringify(r.callTree).includes('HotLoop')) fail('call tree should contain the path to HotLoop');
if (!r.limitations.some(l => l.includes('safe point')) || !r.limitations.some(l => l.includes('call counts'))) fail('sampling limits must be stated');
if (r.notes.some(n => n.includes('single run'))) fail('three runs are not a single run');
console.log(`CPU verified: 3 real traces; HotLoop ${(hot.ownShare * 100).toFixed(0)}% (range ${(hot.minOwnShare * 100).toFixed(0)}–${(hot.maxOwnShare * 100).toFixed(0)}%) resolved to Busy/Program.cs; call tree, limits and measured-only finding.`);
NODE

# --- Counters: allocation vs retention, GC pauses, thread-pool pressure, lock contention, and a control that must stay silent
for mode in cpu alloc leak starve contention; do
  engine profile counters "$WORK/repo" --program "$APP" --args "$mode 12" --allow-execution --duration 15 > "$WORK/ctr-$mode.json"
done
node - "$WORK" <<'NODE'
const w = process.argv[2];
const { load, fail } = require(w + '/lib.cjs');
const L = m => load(`${w}/ctr-${m}.json`);
const rules = r => r.findings.map(f => f.ruleId);
const series = (r, n) => r.series.find(s => s.name === n);
const [cpu, alloc, leak, starve, contention] = ['cpu', 'alloc', 'leak', 'starve', 'contention'].map(L);
for (const r of [cpu, alloc, leak, starve, contention]) if (r.status !== 'completed' || r.series.length < 6) fail('counters run incomplete: ' + r.status);

// allocation is not retention
if (series(alloc, 'allocationRate').mean < 1000) fail('alloc workload should allocate > 1 GiB/s, got ' + series(alloc, 'allocationRate').mean);
if (series(alloc, 'heapAfterGc').last > 64) fail('alloc workload retains nothing: post-GC heap should stay small');
if (!rules(alloc).includes('PRF0102') || rules(alloc).includes('PRF0101')) fail('alloc: PRF0102 yes, PRF0101 (retention) no: ' + rules(alloc));
if (!alloc.findings.find(f => f.ruleId === 'PRF0102').description.includes('churn')) fail('PRF0102 must say it is churn, not retention');
if (!rules(alloc).includes('PRF0103')) fail('heavy allocation should show GC pause time');
// retention
if (!rules(leak).includes('PRF0101') || !(series(leak, 'heapAfterGc').last > 3 * series(leak, 'heapAfterGc').min)) fail('leak: sustained post-GC heap growth expected: ' + rules(leak));
if (!leak.findings.find(f => f.ruleId === 'PRF0101').limitations.some(l => l.includes('not proof'))) fail('PRF0101 must not claim a leak is proven');
// thread-pool starvation, linked to the static suspicion
const pressure = starve.findings.find(f => f.ruleId === 'PRF0201');
if (!pressure || pressure.validation !== 'measured' || !pressure.relatedFindingIds?.length || !pressure.limitations.some(l => l.includes('candidates'))) fail('starve: PRF0201 linked to static blocking findings as candidates, not causes');
// contention
if (!rules(contention).includes('PRF0202') || contention.totals.lockContentions < 100) fail('contention: expected PRF0202 with >= 100 contentions: ' + JSON.stringify(contention.totals));
// control: a pure CPU loop must trigger none of the memory / concurrency findings
for (const id of ['PRF0101', 'PRF0102', 'PRF0103', 'PRF0201', 'PRF0202']) if (rules(cpu).includes(id)) fail('false positive on the CPU control: ' + id);
console.log('Counters verified: churn ≠ retention, sustained growth flagged as consistent-with-leak (not proof), GC pause share, thread-pool pressure linked to static candidates, lock contention, and a silent control.');
NODE

# --- Heap: retained growth must be found in a leaking process and not invented for churn
engine profile heap "$WORK/repo" --program "$APP" --args "leak 30" --allow-execution --snapshots 2 --warmup-seconds 3 --interval-seconds 6 > "$WORK/heap-leak.json"
engine profile heap "$WORK/repo" --program "$APP" --args "alloc 30" --allow-execution --snapshots 2 --warmup-seconds 3 --interval-seconds 6 > "$WORK/heap-alloc.json"
node - "$WORK/heap-leak.json" "$WORK/heap-alloc.json" "$WORK" <<'NODE'
const { load, fail } = require(process.argv[4] + '/lib.cjs');
const [leak, alloc] = process.argv.slice(2, 4).map(load);
if (leak.status !== 'completed' || leak.snapshots.length !== 2) fail('two snapshots expected: ' + leak.status + ' ' + leak.notes);
if (leak.notes.some(n => n.includes('differ'))) fail('snapshot accounting: ' + leak.notes);
if (leak.snapshots[1].totalBytes < 1.5 * leak.snapshots[0].totalBytes) fail('the leaking process must have a larger reachable heap in the second snapshot');
if (leak.growth[0].type !== 'System.Byte[]' || leak.growth[0].countAfter < 2 * leak.growth[0].countBefore) fail('System.Byte[] should lead the growth: ' + JSON.stringify(leak.growth[0]));
const f = leak.findings.find(x => x.ruleId === 'PRF0301');
if (!f || f.validation !== 'measured' || !f.limitations.some(l => l.includes('retention paths'))) fail('PRF0301 with its limitation');
if (alloc.status !== 'completed' || alloc.findings.some(x => x.ruleId === 'PRF0301')) fail('allocation churn must not be reported as retained growth');
console.log(`Heap verified: reachable bytes ${(leak.snapshots[0].totalBytes / 1e6).toFixed(0)} → ${(leak.snapshots[1].totalBytes / 1e6).toFixed(0)} MB, System.Byte[] leads the growth; churn produces no finding.`);
NODE

# --- Comparison: only repeated, non-overlapping measurements can call a regression
engine profile cpu "$WORK/repo" --program "$APP" --args "fixed 12" --allow-execution --repeat 3 > "$WORK/cmp-small-a.json"
engine profile cpu "$WORK/repo" --program "$APP" --args "fixed 12" --allow-execution --repeat 3 > "$WORK/cmp-small-b.json"
engine profile cpu "$WORK/repo" --program "$APP" --args "fixed 60" --allow-execution --repeat 3 > "$WORK/cmp-big.json"
engine profile cpu "$WORK/repo" --program "$APP" --args "fixed 12" --allow-execution --repeat 1 > "$WORK/cmp-single.json"
engine profile cpu "$WORK/repo" --program "$APP" --args "fixed 60" --allow-execution --repeat 1 > "$WORK/cmp-single-big.json"
engine profile compare "$WORK/cmp-small-a.json" "$WORK/cmp-small-b.json" > "$WORK/c-same.json"
engine profile compare "$WORK/cmp-small-a.json" "$WORK/cmp-big.json" > "$WORK/c-regress.json"
engine profile compare "$WORK/cmp-big.json" "$WORK/cmp-small-a.json" > "$WORK/c-improve.json"
engine profile compare "$WORK/cmp-single.json" "$WORK/cmp-single-big.json" > "$WORK/c-single.json"
CODE=0; engine profile compare "$WORK/cmp-small-a.json" "$WORK/ctr-alloc.json" > /dev/null 2>&1 || CODE=$?
[[ "$CODE" == "2" ]] || fail "comparing reports of different kinds must be refused (exit 2), got $CODE"
node - "$WORK" <<'NODE'
const w = process.argv[2];
const { load, fail } = require(w + '/lib.cjs');
const [same, reg, imp, single] = ['c-same', 'c-regress', 'c-improve', 'c-single'].map(n => load(`${w}/${n}.json`));
if (same.verdict !== 'noSignificantChange') fail('the same workload twice must not differ: ' + JSON.stringify(same.deltas.filter(d => d.verdict !== 'noSignificantChange')));
const total = c => c.deltas.find(d => d.subject === 'total sampled CPU ms');
if (reg.verdict !== 'regression' || total(reg).verdict !== 'regression') fail('5x the work with 3 disjoint runs per side is a regression: ' + JSON.stringify(total(reg)));
if (imp.verdict !== 'improvement') fail('the reverse is an improvement: ' + imp.verdict);
if (single.verdict !== 'inconclusive' || total(single).verdict !== 'inconclusive' || !total(single).reason.includes('single run')) fail('one run per side must be inconclusive, never a verdict: ' + JSON.stringify(total(single)));
if (!reg.notes.some(n => n.includes('simpler-looking'))) fail('the comparison must warn against judging by code shape');
console.log(`Comparison verified: same = ${same.verdict}; 5× work = ${reg.verdict} (${total(reg).changePercent}%); reverse = ${imp.verdict}; single runs = ${single.verdict}; mixed kinds refused.`);
NODE

# --- Attach: counters from an already-running process; it must be left running
dotnet "$APP" alloc 60 > "$WORK/target.log" 2>&1 &
TARGET=$!
for _ in $(seq 1 50); do grep -q ready "$WORK/target.log" && break; sleep 0.2; done
engine profile counters "$WORK/repo" --attach "$TARGET" --allow-attach --duration 6 > "$WORK/attach.json"
kill -0 "$TARGET" 2>/dev/null || fail "attaching must not stop the observed process"
kill "$TARGET" 2>/dev/null || true; wait "$TARGET" 2>/dev/null || true
node -e '
const r = JSON.parse(require("fs").readFileSync(process.argv[1]));
if (r.status !== "completed" || !r.series.find(s => s.name === "allocationRate" && s.samples >= 3 && s.mean > 1000)) { console.error("Profiling test failed: attach should measure the running allocator: " + r.status); process.exit(1); }
console.log("Attach verified: counters read from a running process (" + r.series.find(s => s.name === "allocationRate").mean.toFixed(0) + " MiB/s) and the process was left running.");' "$WORK/attach.json"

# --- Cancellation: SIGTERM stops the tracer and the profiled program
dotnet "$ENGINE" profile cpu "$WORK/repo" --program "$APP" --args "cpu 40" --allow-execution > "$WORK/cancel.json" &
PID=$!
sleep 6
kill -TERM "$PID"
CODE=0; wait "$PID" || CODE=$?
sleep 2
[[ "$CODE" == "130" ]] || fail "SIGTERM during a profile must exit 130, got $CODE"
pgrep -f "$APP" >/dev/null && fail "the profiled program must not survive a cancelled profile"
echo "Cancellation verified: SIGTERM ended the profile (130) and left no orphan process."

echo "Profiling test passed."
