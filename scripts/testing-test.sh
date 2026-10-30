#!/usr/bin/env bash
# Phase 4a gate: real test execution through dotnet test + TRX for xUnit, NUnit and MSTest; honest reporting of build failures, timeouts and
# skipped tests; explicit authorization before executing code; related/failed-only selection; run comparison; stack traces resolved against the graph.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj"
FIX="$ROOT/tests/fixtures/testing"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/codeatlas-testing.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
export CODEATLAS_CACHE_DIR="$WORK/cache" CODEATLAS_DATA_DIR="$WORK/data"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

dotnet build "$PROJECT" --nologo --verbosity quiet >/dev/null
ENGINE="$(dirname "$PROJECT")/bin/Debug/net10.0/CodeAtlas.Engine.dll"
engine() { dotnet "$ENGINE" "$@"; }
fail() { echo "Testing test failed: $1" >&2; exit 1; }
copy_fixture() { mkdir -p "$1"; rsync -a --exclude obj --exclude bin --exclude TestResults "$FIX/" "$1/"; }

cat > "$WORK/lib.cjs" <<'JS'
const fs = require('node:fs');
exports.load = p => JSON.parse(fs.readFileSync(p, 'utf8'));
exports.fail = m => { console.error('Testing test failed: ' + m); process.exit(1); };
JS

# 1. Static discovery
engine tests discover "$FIX" > "$WORK/discover.json"
node - "$WORK/discover.json" "$WORK" <<'NODE'
const { load, fail } = require(process.argv[3] + '/lib.cjs');
const r = load(process.argv[2]);
if (r.tests.length !== 12) fail('expected 12 test methods across the three frameworks, got ' + r.tests.length);
for (const fw of ['xunit', 'nunit', 'mstest']) {
  const t = r.tests.filter(x => x.framework === fw);
  if (t.length !== 4) fail(`${fw}: expected 4 tests`);
  if (t.filter(x => x.skipped && x.skipReason === 'not ready').length !== 1) fail(`${fw}: skipped test with reason`);
  if (t.filter(x => x.cases === 2).length !== 1) fail(`${fw}: data rows should be counted`);
}
console.log('Discovery verified: 12 tests in xUnit, NUnit and MSTest with skip reasons and data-row counts.');
NODE

# 2. Execution needs authorization and executes nothing without it
CODE=0; engine tests run "$FIX" > "$WORK/auth.json" || CODE=$?
[[ "$CODE" == "7" ]] || fail "tests run without --allow-execution must exit 7, got $CODE"
[[ ! -d "$WORK/data/testruns" ]] || fail "a refused run must not create results"
node -e 'const r=JSON.parse(require("fs").readFileSync(process.argv[1]));if(r.status!=="authorizationRequired"||r.projects.length!==3){process.exit(1)}' "$WORK/auth.json" || fail "authorization plan"
echo "Authorization verified: without --allow-execution the plan is shown (exit 7) and nothing runs."

# 3. A real run: every outcome must come from a TRX
engine tests run "$FIX" --allow-execution --timeout-seconds 400 > "$WORK/run.json"
node - "$WORK/run.json" "$WORK" <<'NODE'
const fs = require('node:fs');
const { load, fail } = require(process.argv[3] + '/lib.cjs');
const r = load(process.argv[2]);
if (r.status !== 'completedWithFailures') fail('status ' + r.status);
const s = r.summary;
if (s.passed !== 9 || s.failed !== 3 || s.notExecuted !== 3 || r.notRun.length) fail('summary ' + JSON.stringify(s) + ' notRun ' + r.notRun);
for (const p of r.projects) {
  if (p.status !== 'completed' || !p.trxFile || !fs.existsSync(p.trxFile)) fail(p.project + ': no TRX on disk');
  const trx = fs.readFileSync(p.trxFile, 'utf8');
  if ((trx.match(/<UnitTestResult /g) || []).length !== p.results.length) fail(p.project + ': results do not match the TRX');
  if (!p.command.startsWith('dotnet test')) fail('command recorded');
  const failed = p.results.filter(t => t.outcome === 'failed');
  if (failed.length !== 1 || !/divide by zero/i.test(failed[0].message)) fail(p.project + ': the seeded failure must be reported with its message');
  const frames = failed[0].frames;
  const names = frames.filter(f => f.resolution === 'resolved').map(f => f.symbol.split('(')[0].split('.').slice(-2).join('.'));
  if (names[0] !== 'Calculator.Compute' || names[1] !== 'Calculator.Divide') fail(p.project + ': stack frames should resolve to Calculator.Compute < Calculator.Divide, got ' + names);
  if (frames[0].file !== 'Calc/Calculator.cs' || frames[0].line !== 10) fail(p.project + ': top frame location ' + frames[0].file + ':' + frames[0].line);
  if (p.results.some(t => t.outcome === 'passed' && t.name.includes('Skipped'))) fail('a skipped test must never be reported as passed');
}
const f = r.findings.filter(x => x.ruleId === 'TST0001');
if (f.length !== 3 || f.some(x => x.origin !== 'test' || x.validation !== 'runtimeObserved' || x.location.file !== 'Calc/Calculator.cs' || !x.callPath || x.callPath.length < 3 || !x.reproduction.includes('--filter'))) fail('failure findings', f.map(x => x.title));
if (!r.coverage || !(r.coverage.lineRate > 0 && r.coverage.lineRate <= 1)) fail('coverage should be read from the Cobertura report of the xUnit project');
console.log('Run verified: 9 passed / 3 failed / 3 not executed, all from TRX; failures resolved to Calculator.Compute (Calc/Calculator.cs:10); coverage read.');
NODE

# 4. Related tests and repeating failures
engine tests related "$FIX" "Calculator.Compute" > "$WORK/related.json"
node - "$WORK/related.json" "$WORK" <<'NODE'
const { load, fail } = require(process.argv[3] + '/lib.cjs');
const r = load(process.argv[2]);
if (r.tests.length !== 3 || !r.tests.every(t => t.node.symbol.endsWith('Divide_by_zero_fails()') && t.certainty === 'proven')) fail('only the three Divide tests reach Calculator.Compute', r.tests.map(t => t.node.symbol));
if (!r.filter || r.filter.includes('Add_works')) fail('filter must not select unrelated tests: ' + r.filter);
console.log('Related tests verified: Calculator.Compute is reached by exactly the three Divide tests.');
NODE
engine tests run "$FIX" --allow-execution --related Calculator.Compute --timeout-seconds 400 > "$WORK/run-related.json"
engine tests run "$FIX" --allow-execution --failed-from "$WORK/run.json" --timeout-seconds 400 > "$WORK/run-failed.json"
node - "$WORK/run-related.json" "$WORK/run-failed.json" "$WORK" <<'NODE'
const { load, fail } = require(process.argv[4] + '/lib.cjs');
for (const [file, label] of [[process.argv[2], 'related'], [process.argv[3], 'failed-only']]) {
  const r = load(file);
  const all = r.projects.flatMap(p => p.results);
  if (r.summary.passed !== 0 || r.summary.failed !== 3 || all.length !== 3 || !all.every(t => t.method === 'Divide_by_zero_fails')) fail(`${label} run should execute exactly the 3 Divide tests: ` + JSON.stringify(r.summary));
}
console.log('Selective runs verified: --related and --failed-from executed only the three failing tests.');
NODE

# 5. Comparison between runs (fix the defect in a copy, rerun, compare in both directions)
copy_fixture "$WORK/fixed"
sed -i.bak 's|public int Divide(int a, int b) => Compute(a, b);|public int Divide(int a, int b) => b == 0 ? 0 : Compute(a, b);|' "$WORK/fixed/Calc/Calculator.cs" && rm "$WORK/fixed/Calc/Calculator.cs.bak"
grep -q 'b == 0' "$WORK/fixed/Calc/Calculator.cs" || fail "could not patch the copy"
engine tests run "$WORK/fixed" --allow-execution --timeout-seconds 400 > "$WORK/run-fixed.json"
engine tests compare "$WORK/run.json" "$WORK/run-fixed.json" > "$WORK/cmp-fix.json"
engine tests compare "$WORK/run-fixed.json" "$WORK/run.json" > "$WORK/cmp-regress.json"
node - "$WORK/run-fixed.json" "$WORK/cmp-fix.json" "$WORK/cmp-regress.json" "$WORK" <<'NODE'
const { load, fail } = require(process.argv[5] + '/lib.cjs');
const fixed = load(process.argv[2]), fix = load(process.argv[3]), regress = load(process.argv[4]);
if (fixed.status !== 'completed' || fixed.summary.failed !== 0 || fixed.summary.passed !== 12) fail('the patched copy should pass 12 tests: ' + JSON.stringify(fixed.summary));
if (fix.fixed.length !== 3 || fix.regressions.length !== 0) fail('baseline -> fixed: 3 fixed, 0 regressions');
if (regress.regressions.length !== 3 || regress.fixed.length !== 0) fail('fixed -> baseline: 3 regressions, 0 fixed');
console.log('Comparison verified: the same three tests are "fixed" one way and "regressions" the other.');
NODE

# 6. Honesty when tests cannot run: build error and timeout
copy_fixture "$WORK/broken"
printf 'this is not C#' >> "$WORK/broken/Calc/Calculator.cs"
engine tests run "$WORK/broken" --allow-execution --timeout-seconds 400 > "$WORK/run-broken.json"
engine tests run "$FIX" --allow-execution --project Calc.XUnit --timeout-seconds 1 > "$WORK/run-timeout.json"
node - "$WORK/run-broken.json" "$WORK/run-timeout.json" "$WORK" <<'NODE'
const { load, fail } = require(process.argv[4] + '/lib.cjs');
const broken = load(process.argv[2]), timeout = load(process.argv[3]);
if (broken.status === 'completed' || broken.summary.passed !== 0 || broken.projects.some(p => p.status !== 'buildFailed' || p.results.length)) fail('a build failure must yield no results, status buildFailed: ' + JSON.stringify(broken.projects.map(p => p.status)));
if (broken.findings.filter(f => f.ruleId === 'TST0002').length !== 3 || broken.notRun.length !== 12) fail('every discovered test must be listed as not run');
if (timeout.projects[0].status !== 'timedOut' || timeout.summary.passed !== 0 || timeout.projects[0].results.length) fail('a timed-out run must report no results');
console.log('Honesty verified: build errors and timeouts produce explicit statuses, zero passed tests and a not-run list.');
NODE

# 7. Exceptions: real trace from the failing test, plus synthetic traces for inner exceptions, async/lambda frames and ambiguity
node -e 'const r=JSON.parse(require("fs").readFileSync(process.argv[1]));const t=r.projects[2].results.find(x=>x.outcome==="failed");console.log(t.message.split("\n")[0]+"\n"+t.stackTrace)' "$WORK/run.json" > "$WORK/trace.txt"
engine exceptions resolve "$FIX" --trace-file "$WORK/trace.txt" > "$WORK/exc.json"
cat > "$WORK/shop-trace.txt" <<'TRACE'
System.InvalidOperationException: Order failed
 ---> System.DivideByZeroException: Attempted to divide by zero.
   at Shop.Application.OrderService.Process(Int32 value)
   at Shop.Application.OrderService.<Place>d__5.MoveNext()
   --- End of inner exception stack trace ---
   at Shop.Web.OrdersController.Get(Int32 id) in Web/Program.cs:line 27
   at System.Runtime.CompilerServices.AsyncTaskMethodBuilder.Start[TStateMachine](TStateMachine& stateMachine)
TRACE
printf 'System.Exception: boom\n   at Microsoft.AspNetCore.Hosting.Foo.Bar()\n   at System.Threading.Tasks.Task.Run()\n' > "$WORK/framework-trace.txt"
engine exceptions resolve "$ROOT/tests/fixtures/callgraph/shop" --trace-file "$WORK/shop-trace.txt" > "$WORK/exc-shop.json"
engine exceptions resolve "$ROOT/tests/fixtures/callgraph/shop" --trace-file "$WORK/framework-trace.txt" > "$WORK/exc-fw.json"
echo "just some log text" | engine exceptions resolve "$ROOT/tests/fixtures/callgraph/shop" --trace-file - > "$WORK/exc-none.json"
node - "$WORK/exc.json" "$WORK/exc-shop.json" "$WORK/exc-fw.json" "$WORK/exc-none.json" "$WORK" <<'NODE'
const { load, fail } = require(process.argv[6] + '/lib.cjs');
const [real, shop, fw, none] = process.argv.slice(2, 6).map(load);
if (real.status !== 'resolved' || !real.originNode.symbol.endsWith('Calculator.Compute(int, int)')) fail('real trace origin: ' + real.originNode?.symbol);
if (!real.relatedTests.some(t => t.node.symbol.includes('Divide_by_zero_fails'))) fail('the failing test must be listed as related');
const f = real.findings[0];
if (f.ruleId !== 'ATL4001' || f.origin !== 'dynamic' || f.validation !== 'runtimeObserved' || f.location.file !== 'Calc/Calculator.cs' || f.location.line !== 10 || !f.callPath.length) fail('exception finding');
if (shop.segments.length !== 2 || !shop.segments[0].type.endsWith('InvalidOperationException') || !shop.segments[1].isInner || !shop.segments[1].type.endsWith('DivideByZeroException')) fail('inner exception must be a separate segment: ' + JSON.stringify(shop.segments.map(s => s.type)));
const inner = shop.segments[1].frames, outer = shop.segments[0].frames;
if (inner[0].resolution !== 'ambiguous') fail('Process(Int32) matches two overloads of the same arity: it must be ambiguous, not guessed');
if (inner[1].resolution !== 'resolved') fail('async state-machine frame <Place>d__5.MoveNext should resolve to Place');
if (outer[0].resolution !== 'resolved' || outer[1].resolution === 'resolved') fail('controller frame resolves; framework frame does not');
if (shop.originFrame.symbol.includes('Place') === false) fail('origin = first resolvable frame of the innermost exception');
if (fw.status !== 'unresolved' || fw.originNode !== null) fail('a framework-only trace is unresolved');
if (none.status !== 'noFrames') fail('text without frames');
console.log('Exceptions verified: real trace → Calculator.Compute with the failing test; inner segments, async frames, ambiguity, framework-only and empty input handled honestly.');
NODE

echo "Testing test passed."
