#!/usr/bin/env bash
# Phase 4b gate: a real debugging session against netcoredbg (breakpoints, conditions, stepping, locals with redaction, evaluate, function and
# exception breakpoints, exception analysis, attach, termination). Without the adapter installed the session tests are SKIPPED loudly,
# while the authorization and "debugger missing" behaviours are always verified.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj"
FIX="$ROOT/tests/fixtures/debugging/App"
WORK="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/codeatlas-debug.XXXXXX")" && pwd -P)"
trap 'pkill -f "$WORK/out/App.dll" 2>/dev/null || true; rm -rf "$WORK"' EXIT
export CODEATLAS_CACHE_DIR="$WORK/cache"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

dotnet build "$PROJECT" --nologo --verbosity quiet >/dev/null
ENGINE="$(dirname "$PROJECT")/bin/Debug/net10.0/CodeAtlas.Engine.dll"
engine() { dotnet "$ENGINE" "$@"; }
fail() { echo "Debugging test failed: $1" >&2; exit 1; }

# Always: a missing debugger is an explicit error, never a fake session.
mkdir -p "$WORK/emptyhome"
CODE=0; HOME="$WORK/emptyhome" CODEATLAS_NETCOREDBG="" PATH="/usr/bin:/bin:$(dirname "$(command -v dotnet)")" dotnet "$ENGINE" debug doctor > "$WORK/doctor-missing.json" || CODE=$?
[[ "$CODE" == "5" ]] || fail "debug doctor without the adapter must exit 5, got $CODE"
grep -q '"debuggerNotInstalled"' "$WORK/doctor-missing.json" || fail "doctor must report debuggerNotInstalled"
echo "Missing debugger verified: explicit debuggerNotInstalled (exit 5) with install instructions."

mkdir -p "$WORK/repo" && cp -R "$FIX" "$WORK/repo/App"
CODE=0; engine debug run "$WORK/repo" --program "$WORK/out/App.dll" > "$WORK/auth.json" || CODE=$?
[[ "$CODE" == "7" ]] || fail "debug run without --allow-execution must exit 7, got $CODE"
CODE=0; engine debug run "$WORK/repo" --attach 1 > "$WORK/auth2.json" || CODE=$?
[[ "$CODE" == "7" ]] || fail "attach without --allow-attach must exit 7, got $CODE"
echo "Authorization verified: launching needs --allow-execution and attaching needs --allow-attach (exit 7, nothing started)."

engine debug doctor > "$WORK/doctor.json" || true
if ! grep -q '"ready"' "$WORK/doctor.json"; then
  echo "SKIPPED: netcoredbg is not installed (see 'debug doctor'); live-session tests did not run."
  exit 0
fi

(cd "$WORK/repo/App" && dotnet build -c Debug -o "$WORK/out" --nologo -v q >/dev/null)
PROGRAM_LINE="$(grep -n 'total += Work.Square(i);' "$WORK/repo/App/Program.cs" | cut -d: -f1)"
WRITE_LINE="$(grep -n 'Console.WriteLine(\$"total' "$WORK/repo/App/Program.cs" | cut -d: -f1)"

cat > "$WORK/driver.cjs" <<'JS'
const { spawn } = require('node:child_process');
const [engine, root, dll, loopLine, writeLine] = process.argv.slice(2);
const fail = m => { console.error('Debugging test failed: ' + m); process.exit(1); };
const p = spawn('dotnet', [engine, 'debug', 'run', root, '--program', dll, '--allow-execution', '--stop-timeout-seconds', '40', '--record', process.env.RECORD]);
let buf = '', waiters = [], id = 0; const lines = [];
p.stdout.on('data', d => { buf += d; let i; while ((i = buf.indexOf('\n')) >= 0) { const l = buf.slice(0, i); buf = buf.slice(i + 1); try { lines.push(JSON.parse(l)); waiters.splice(0).forEach(w => w()); } catch { fail('non-JSON output: ' + l); } } });
const wait = pred => new Promise(res => { const t = () => { const f = lines.find(pred); if (f) { lines.splice(lines.indexOf(f), 1); res(f); } else waiters.push(t); }; t(); });
const send = async c => { const n = ++id; p.stdin.write(JSON.stringify({ id: n, ...c }) + '\n'); const r = await wait(m => m.id === n); return r; };
const ok = async (c) => { const r = await send(c); if (!r.ok) fail(`${c.cmd}: ${r.error}`); return r.result; };
const top = s => s.frames[0];
const local = (s, name) => s.locals.flatMap(sc => sc.variables).find(v => v.name === name);
const timer = setTimeout(() => fail('session timed out'), 150000);

(async () => {
  const ready = await wait(m => m.event);
  if (ready.event !== 'ready' || !ready.graphResolution) fail('session did not start with call-graph resolution: ' + JSON.stringify(ready));
  if (!ready.state.supports.conditionalBreakpoints || ready.state.supports.asyncTaskInspection !== false) fail('capabilities must be reported honestly');

  // conditional breakpoint: i == 3 must skip i = 1, 2
  const bp = await ok({ cmd: 'breakpoints', file: 'App/Program.cs', items: [{ line: +loopLine, condition: 'i == 3' }] });
  if (bp.breakpoints.length !== 1 || typeof bp.breakpoints[0].verified !== 'boolean') fail('breakpoint reply');
  let s = await ok({ cmd: 'run' });
  if (s.state !== 'stopped' || s.reason !== 'breakpoint') fail('expected a breakpoint stop: ' + JSON.stringify(s).slice(0, 300));
  if (top(s).file !== 'App/Program.cs' || top(s).line !== +loopLine || top(s).resolution !== 'resolved' || !top(s).name.includes('Main')) fail('top frame: ' + JSON.stringify(top(s)));
  if (local(s, 'i')?.value !== '3' || local(s, 'total')?.value !== '5') fail(`condition i == 3 was not honored (i=${local(s, 'i')?.value}, total=${local(s, 'total')?.value})`);
  if (local(s, 'secret')?.value !== '<redacted>' || local(s, 'secret').redacted !== true) fail('sensitive variable names must be redacted');

  const evaluated = await ok({ cmd: 'evaluate', expression: 'total * 2', frameId: top(s).id });
  if (evaluated.result !== '10') fail('evaluate total * 2 should be 10, got ' + evaluated.result);

  // stepping: into Square, out to Main
  s = await ok({ cmd: 'stepIn' });
  if (!top(s).name.includes('Square') || top(s).resolution !== 'resolved' || !s.frames[1].name.includes('Main')) fail('stepIn should enter Work.Square: ' + JSON.stringify(s.frames.slice(0, 2)));
  if (local(s, 'n')?.value !== '3') fail('argument n should be 3');
  s = await ok({ cmd: 'stepOut' });
  if (!top(s).name.includes('Main')) fail('stepOut should return to Main');
  const before = top(s).line;
  s = await ok({ cmd: 'next' });
  if (s.state !== 'stopped' || !top(s).name.includes('Main')) fail('next should stay in Main');

  // function breakpoint (clear the line breakpoint first)
  await ok({ cmd: 'breakpoints', file: 'App/Program.cs', items: [] });
  const fb = await ok({ cmd: 'functionBreakpoints', names: ['DebugApp.Work.Fail'] });
  if (fb.breakpoints.length !== 1) fail('function breakpoint reply');
  s = await ok({ cmd: 'continue' });
  if (s.reason !== 'function breakpoint' && !top(s).name.includes('Fail')) fail('function breakpoint should stop in Work.Fail: ' + JSON.stringify(s).slice(0, 300));
  if (local(s, 'reason')?.value !== '"handled"') fail('argument reason should be "handled", got ' + local(s, 'reason')?.value);

  // exception breakpoint on a first-chance (caught) exception, with analysis against the call graph
  await ok({ cmd: 'functionBreakpoints', names: [] });
  await ok({ cmd: 'exceptions', filters: ['all'] });
  s = await ok({ cmd: 'continue' });
  if (s.reason !== 'exception' || !s.exception) fail('expected an exception stop: ' + JSON.stringify(s).slice(0, 300));
  if (!String(s.exception.typeName || s.exception.exceptionId).includes('InvalidOperationException') || s.exception.message !== 'handled') fail('exception details: ' + JSON.stringify(s.exception).slice(0, 300));
  const analysis = s.exception.analysis;
  if (!analysis || analysis.status !== 'resolved' || !analysis.originNode.symbol.includes('Work.Fail') || analysis.findings[0].ruleId !== 'ATL4001') fail('the runtime stack trace should resolve to Work.Fail: ' + JSON.stringify(analysis).slice(0, 300));

  // stop in front of the final line, then run to the end
  await ok({ cmd: 'exceptions', filters: [] });
  await ok({ cmd: 'breakpoints', file: 'App/Program.cs', items: [{ line: +writeLine }] });
  s = await ok({ cmd: 'continue' });
  if (top(s).line !== +writeLine || local(s, 'total')?.value !== '155') fail(`expected to stop at the WriteLine with total 155, got line ${top(s).line} total ${local(s, 'total')?.value}`);
  s = await ok({ cmd: 'continue' });
  if (s.state !== 'exited' || s.exitCode !== 0 || !s.output.some(l => l.includes('total=155'))) fail('program should exit 0 printing total=155: ' + JSON.stringify(s));
  await ok({ cmd: 'terminate' });
  clearTimeout(timer);
  console.log('Session verified: condition i == 3, locals with redaction, evaluate, stepIn/Out/Over, function breakpoint, first-chance exception with call-graph analysis, exit code 0.');
  process.exit(0);
})().catch(e => fail(String(e)));
JS
RECORD="$WORK/timeline.jsonl" node "$WORK/driver.cjs" "$ENGINE" "$WORK/repo" "$WORK/out/App.dll" "$PROGRAM_LINE" "$WRITE_LINE"

# The recording holds exactly what the session captured.
engine debug timeline "$WORK/timeline.jsonl" > "$WORK/timeline.json"
node -e '
const t = JSON.parse(require("fs").readFileSync(process.argv[1]));
const e = t.events;
if (e.length < 7) { console.error("Debugging test failed: expected at least 7 captured events, got " + e.length); process.exit(1); }
if (!e.some(x => x.reason === "exception" && String(x.exceptionType).includes("InvalidOperationException") && x.exceptionMessage === "handled")) { console.error("Debugging test failed: the exception stop must be in the recording"); process.exit(1); }
if (e[e.length - 1].state !== "exited" || e[e.length - 1].exitCode !== 0) { console.error("Debugging test failed: the recording must end with the exit"); process.exit(1); }
if (!e.slice(0, 6).every(x => x.file === "App/Program.cs" && x.line > 0)) { console.error("Debugging test failed: every stop needs a source location"); process.exit(1); }
if (JSON.stringify(t).includes("hunter2")) { console.error("Debugging test failed: a sensitive value leaked into the recording"); process.exit(1); }
console.log("Recording verified: " + e.length + " captured events (stops, exception, exit) with locations; nothing sensitive recorded.");' "$WORK/timeline.json"

# Termination kills the debuggee: SIGTERM on the engine while the program is paused must leave no orphan.
node - "$ENGINE" "$WORK/repo" "$WORK/out/App.dll" "$PROGRAM_LINE" "$WORK" <<'JS'
const { spawn, spawnSync } = require('node:child_process');
const [engine, root, dll, line, work] = process.argv.slice(2);
const fail = m => { console.error('Debugging test failed: ' + m); process.exit(1); };
const p = spawn('dotnet', [engine, 'debug', 'run', root, '--program', dll, '--allow-execution']);
let buf = ''; const lines = []; p.stdout.on('data', d => { buf += d; let i; while ((i = buf.indexOf('\n')) >= 0) { lines.push(JSON.parse(buf.slice(0, i))); buf = buf.slice(i + 1); } });
let done = false;
process.on('exit', () => { if (!done) { console.error('Debugging test failed: the termination scenario did not complete'); process.exitCode = 1; } });
const until = async f => { for (let i = 0; i < 600; i++) { const m = lines.find(f); if (m) return m; await new Promise(r => setTimeout(r, 100)); } fail('timeout'); };
(async () => {
  await until(m => m.event === 'ready');
  p.stdin.write(JSON.stringify({ id: 1, cmd: 'breakpoints', file: 'App/Program.cs', items: [{ line: +line }] }) + '\n'); await until(m => m.id === 1);
  p.stdin.write(JSON.stringify({ id: 2, cmd: 'run' }) + '\n'); const stop = await until(m => m.id === 2);
  if (stop.result.state !== 'stopped') fail('should be paused at the breakpoint');
  const alive = () => spawnSync('pgrep', ['-f', dll]).status === 0;
  if (!alive()) fail('the debuggee should be running while paused');
  p.kill('SIGTERM');
  const code = p.exitCode !== null ? p.exitCode : await new Promise(r => p.on('exit', r));
  await new Promise(r => setTimeout(r, 1500));
  if (code !== 130) fail('SIGTERM should end the session with exit 130, got ' + code);
  if (alive()) fail('the debuggee must not survive the debug session');
  done = true;
  console.log('Termination verified: SIGTERM ended the session (130) and the paused debuggee was killed.');
})();
JS

# Attach by process id: pause, see the stack, detach without killing the process.
dotnet "$WORK/out/App.dll" wait > "$WORK/target.log" 2>&1 &
TARGET=$!
for _ in $(seq 1 50); do grep -q waiting "$WORK/target.log" && break; sleep 0.2; done
node - "$ENGINE" "$WORK/repo" "$TARGET" <<'JS'
const { spawn } = require('node:child_process');
const [engine, root, pid] = process.argv.slice(2);
const fail = m => { console.error('Debugging test failed: ' + m); process.exit(1); };
const p = spawn('dotnet', [engine, 'debug', 'run', root, '--attach', pid, '--allow-attach', '--stop-timeout-seconds', '30']);
let buf = ''; const lines = []; p.stdout.on('data', d => { buf += d; let i; while ((i = buf.indexOf('\n')) >= 0) { lines.push(JSON.parse(buf.slice(0, i))); buf = buf.slice(i + 1); } });
let done = false;
process.on('exit', () => { if (!done) { console.error('Debugging test failed: the attach scenario did not complete'); process.exitCode = 1; } });
const until = async f => { for (let i = 0; i < 400; i++) { const m = lines.find(f); if (m) return m; await new Promise(r => setTimeout(r, 100)); } fail('timeout'); };
(async () => {
  const ready = await until(m => m.event); if (ready.event !== 'ready') fail('attach session: ' + JSON.stringify(ready));
  p.stdin.write(JSON.stringify({ id: 1, cmd: 'run' }) + '\n'); await until(m => m.id === 1);
  p.stdin.write(JSON.stringify({ id: 2, cmd: 'pause' }) + '\n'); const s = (await until(m => m.id === 2));
  if (!s.ok || s.result.state !== 'stopped') fail('pause on attach: ' + JSON.stringify(s).slice(0, 300));
  p.stdin.write(JSON.stringify({ id: 3, cmd: 'terminate' }) + '\n'); await until(m => m.id === 3);
  if (p.exitCode === null) await new Promise(r => p.on('exit', r));
  done = true;
  console.log('Attach verified: paused a running process by pid and detached.');
})();
JS
sleep 1
kill -0 "$TARGET" 2>/dev/null || fail "detaching must not kill the attached process"
kill "$TARGET" 2>/dev/null || true
wait "$TARGET" 2>/dev/null || true

echo "Debugging test passed."
