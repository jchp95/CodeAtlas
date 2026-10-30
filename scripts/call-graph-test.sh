#!/usr/bin/env bash
# Phase 3 gate: the semantic call graph, cycle/architecture checks, change impact, interprocedural rules and the
# shared business-flow projection must be correct on positive AND negative fixtures, reproducible, cache-correct,
# cancellable, and honest about what they cannot see.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj"
FIX="$ROOT/tests/fixtures/callgraph"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/codeatlas-graph.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
export CODEATLAS_CACHE_DIR="$WORK/cache"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

dotnet build "$PROJECT" --nologo --verbosity quiet >/dev/null
ENGINE="$(dirname "$PROJECT")/bin/Debug/net10.0/CodeAtlas.Engine.dll"
engine() { dotnet "$ENGINE" "$@"; }
fail() { echo "Call graph test failed: $1" >&2; exit 1; }

cat > "$WORK/lib.cjs" <<'JS'
const fs = require('node:fs');
exports.load = p => JSON.parse(fs.readFileSync(p, 'utf8'));
exports.fail = m => { console.error('Call graph test failed: ' + m); process.exit(1); };
exports.index = g => {
  const byId = new Map(g.nodes.map(n => [n.id, n]));
  const name = n => `${n.containingType}.${n.signature}`;
  const find = (type, sig) => { const m = g.nodes.filter(n => !n.isExternal && n.containingType?.endsWith('.' + type) && n.signature === sig);
    if (m.length !== 1) exports.fail(`expected exactly one node ${type}.${sig}, found ${m.length}`); return m[0]; };
  const out = n => g.edges.filter(e => e.from === n.id);
  const edge = (from, to, res) => out(from).find(e => e.to === to.id && (!res || e.resolution === res));
  return { byId, name, find, out, edge };
};
JS

# ---------------------------------------------------------------- 1. shop graph: structure, resolution classes, completeness
engine graph build "$FIX/shop" > "$WORK/shop.json" || fail "graph build exited non-zero"
node - "$WORK/shop.json" "$WORK" <<'NODE'
const { load, fail, index } = require(process.argv[3] + '/lib.cjs');
const g = load(process.argv[2]);
const { byId, find, out, edge } = index(g);

if (g.schemaVersion !== 1 || g.status !== 'completed') fail('envelope: ' + g.status);
if (new Set(g.nodes.map(n => n.id)).size !== g.nodes.length) fail('duplicate node ids');
if (new Set(g.edges.map(e => e.id)).size !== g.edges.length) fail('duplicate edge ids');
for (const e of g.edges) {
  if (!byId.has(e.from)) fail('edge from unknown node ' + e.from);
  if (e.to !== null && !byId.has(e.to)) fail('dangling edge target ' + e.to);
  if (!(e.site.line > 0) || !e.site.file) fail('edge without call-site location');
  if (e.to === null && e.certainty !== 'unknown') fail('a null target must be certainty unknown');
}
if (g.warnings.some(w => w.includes('stub'))) fail('stub nodes: some call targets have no declaring node: ' + g.warnings);
if (g.nodes.some(n => n.tags.includes('stub'))) fail('stub node present');
if (g.projects.length !== 5) fail('projects: ' + g.projects);
if (JSON.stringify(g.projectReferences.find(r => r[0] === 'Shop.Application')) !== JSON.stringify(['Shop.Application', 'Shop.Domain', 'Shop.Infrastructure'])) fail('project references');

// direct calls and recursion
const place = find('OrderService', 'Place(Order)'), addLine = find('Order', 'AddLine(decimal)');
const placeToAdd = edge(place, addLine, 'direct');
if (!placeToAdd || placeToAdd.certainty !== 'proven') fail('direct call Place -> AddLine missing');
const fact = find('OrderService', 'Factorial(int)');
if (!edge(fact, fact, 'direct')) fail('self recursion edge missing');
const even = find('OrderService', 'IsEven(int)'), odd = find('OrderService', 'IsOdd(int)');
if (!edge(even, odd, 'direct') || !edge(odd, even, 'direct')) fail('mutual recursion edges missing');

// overloads, generics, extension methods
const pInt = find('OrderService', 'Process(int)'), pStr = find('OrderService', 'Process(string)');
if (pInt.id === pStr.id) fail('overloads collapsed into one node');
const useLambda = find('OrderService', 'UseLambda(IEnumerable<int>)');
const lambda = out(useLambda).find(e => e.resolution === 'lambda');
if (!lambda || byId.get(lambda.to).kind !== 'lambda') fail('lambda node missing');
if (!edge(byId.get(lambda.to), pInt, 'direct') || edge(byId.get(lambda.to), pStr)) fail('lambda body must call Process(int) only');
const useGroup = find('OrderService', 'UseMethodGroup()');
const group = out(useGroup).filter(e => e.resolution === 'delegate');
if (group.length !== 1 || group[0].to !== pInt.id || group[0].certainty !== 'potential') fail('method group must resolve to Process(int) as potential');
if (!find('OrderService', 'Convert<T>(T)')) fail('generic method node');
const twice = find('OrderService', 'Twice(int)');
if (!out(twice).some(e => e.resolution === 'extensionMethod' && byId.get(e.to).name === 'Doubled' && e.certainty === 'proven')) fail('extension method edge');

// interface dispatch: declared member proven, implementations potential, public interface = open world
const oneArea = find('OrderService', 'OneArea(IShape)');
const area = out(oneArea).filter(e => e.resolution === 'interfaceDispatch');
const declared = area.find(e => byId.get(e.to).containingType.endsWith('IShape'));
const impls = area.filter(e => e.certainty === 'potential').map(e => byId.get(e.to).containingType.split('.').pop()).sort();
if (!declared || declared.certainty !== 'proven') fail('declared interface member edge');
if (JSON.stringify(impls) !== '["Circle","Square"]') fail('IShape implementations: ' + impls);
if (!area.every(e => e.openWorld)) fail('a public interface must be flagged open world');
// closed world: an internal interface with two implementations
const audit = out(find('AuditedAction', 'Run()')).filter(e => e.resolution === 'interfaceDispatch');
if (audit.length !== 3 || audit.some(e => e.openWorld)) fail('internal interface should list 3 targets and not be open world');
if (!audit.every(e => e.inLoop)) fail('call inside foreach must be flagged inLoop');

// virtual dispatch and sealed binding
const talk = out(find('OrderService', 'Talk(Animal)')).filter(e => e.resolution === 'virtualDispatch');
const speakers = talk.filter(e => e.certainty === 'potential').map(e => byId.get(e.to).containingType.split('.').pop()).sort();
if (JSON.stringify(speakers) !== '["Cat","Dog","Puppy"]') fail('virtual targets: ' + speakers);
const toPuppy = out(find('OrderService', 'TalkToPuppy(Puppy)'));
if (!toPuppy.some(e => e.resolution === 'direct' && byId.get(e.to).containingType.endsWith('Puppy'))) fail('call on a sealed type must bind directly');

// delegates are explicitly unknown
const cb = out(find('OrderService', 'UseDelegate(Func<int, int>)')).find(e => e.resolution === 'delegate');
if (!cb || cb.to !== null || cb.certainty !== 'unknown') fail('delegate invocation must be an unknown target');
const lambdaShape = out(byId.get(out(find('OrderService', 'TotalArea(IEnumerable<IShape>)')).find(e => e.resolution === 'lambda').to));
if (!lambdaShape.some(e => e.resolution === 'interfaceDispatch')) fail('lambda body dispatch missing');

// partial methods: one node, both locations
const hook = find('PartialThing', 'Hook(int)');
if (!hook.otherLocations || hook.otherLocations.length !== 1) fail('partial method should keep both locations on one node');
if (!edge(find('PartialThing', 'Trigger()'), hook, 'direct')) fail('call to partial method');

// identical names in different namespaces are different symbols
const calcs = g.nodes.filter(n => n.name === 'Compute');
if (calcs.length !== 2 || new Set(calcs.map(c => c.id)).size !== 2 || new Set(calcs.map(c => c.symbolId)).size !== 2) fail('Compute symbols collided');

// cross-project edges bind to real symbols of the other project
const testGet = find('OrderServiceTests', 'Get_returns_order()');
const getNode = find('OrderService', 'Get(int)');
if (!edge(testGet, getNode, 'direct') || getNode.project !== 'Shop.Application' || testGet.project !== 'Shop.Tests') fail('cross-project edge');
if (!testGet.tags.includes('test')) fail('test method tag');

// completeness is never overstated
const c = g.completeness;
if (c.closed || c.delegateInvocations < 1 || c.openWorldDispatchSites < 1) fail('shop graph must not claim completeness: ' + JSON.stringify(c));
if (!c.statement.includes('NOT complete')) fail('completeness statement');

// entry points
const labels = g.entryPoints.map(e => e.label).sort();
if (JSON.stringify(labels) !== JSON.stringify(['GET /api/orders/legacy/{id}', 'GET /api/orders/{id}', 'GET /ping'])) fail('entry points: ' + labels);
if (byId.get(g.entryPoints.find(e => e.label === 'GET /ping').nodeId).kind !== 'lambda') fail('minimal-api entry should be a lambda node');

// dispatch table
const find2 = g.dispatch.find(d => byId.get(d.declared).containingType.endsWith('IOrderRepository') && byId.get(d.declared).name === 'Find');
if (!find2 || find2.targets.length !== 2) fail('dispatch table for IOrderRepository.Find');
console.log(`Graph structure verified: ${g.nodes.length} nodes, ${g.edges.length} edges; direct, recursion, overload, generic, extension, interface, virtual, sealed, delegate, lambda, partial and cross-project cases.`);
NODE

# ---------------------------------------------------------------- 2. determinism and symbol-based identity
engine graph build "$FIX/shop" --no-cache > "$WORK/shop2.json"
node - "$WORK/shop.json" "$WORK/shop2.json" "$WORK" <<'NODE'
const { load, fail } = require(process.argv[4] + '/lib.cjs');
const strip = g => JSON.stringify({ ...g, generatedAt: 0, metrics: 0 });
if (strip(load(process.argv[2])) !== strip(load(process.argv[3]))) fail('two builds of the same sources differ (not reproducible)');
console.log('Reproducibility verified: two builds are byte-identical apart from timestamp and metrics.');
NODE

cp -R "$FIX/shop" "$WORK/shop-shifted"
{ printf '// shifted\n// shifted\n// shifted\n// shifted\n'; cat "$WORK/shop-shifted/Application/OrderService.cs"; } > "$WORK/os.cs" && mv "$WORK/os.cs" "$WORK/shop-shifted/Application/OrderService.cs"
engine graph build "$WORK/shop-shifted" --no-cache > "$WORK/shifted.json"
cp -R "$FIX/shop" "$WORK/shop-renamed"
sed -i.bak 's/Factorial/Fact/g' "$WORK/shop-renamed/Application/OrderService.cs" && rm "$WORK/shop-renamed/Application/OrderService.cs.bak"
engine graph build "$WORK/shop-renamed" --no-cache > "$WORK/renamed.json"
node - "$WORK/shop.json" "$WORK/shifted.json" "$WORK/renamed.json" "$WORK" <<'NODE'
const { load, fail } = require(process.argv[5] + '/lib.cjs');
const [a, b, r] = process.argv.slice(2, 5).map(load);
const ids = g => g.nodes.map(n => n.id).sort(), edgeIds = g => g.edges.map(e => e.id).sort();
if (JSON.stringify(ids(a)) !== JSON.stringify(ids(b)) || JSON.stringify(edgeIds(a)) !== JSON.stringify(edgeIds(b))) fail('a pure line shift changed node or edge identity');
const place = g => g.nodes.find(n => n.signature === 'Place(Order)');
if (place(b).location.line !== place(a).location.line + 4) fail('node location (metadata) must follow the shift');
const gone = ids(a).filter(i => !ids(r).includes(i)), added = ids(r).filter(i => !ids(a).includes(i));
if (gone.length !== 1 || added.length !== 1) fail(`a rename should replace exactly one node identity, replaced ${gone.length}/${added.length}`);
console.log('Identity verified: a 4-line shift kept every node/edge id; renaming one method replaced exactly that one node.');
NODE

# ---------------------------------------------------------------- 3. cache correctness and incremental extraction
engine graph build "$FIX/shop" > "$WORK/c1.json"
engine graph build "$FIX/shop" > "$WORK/c2.json"
cp -R "$FIX/shop" "$WORK/shop-cache"
engine graph build "$WORK/shop-cache" > /dev/null
printf '\n// touched\n' >> "$WORK/shop-cache/Web/Program.cs"
engine graph build "$WORK/shop-cache" > "$WORK/c3.json"
printf '\n// touched\n' >> "$WORK/shop-cache/Domain/Order.cs"
engine graph build "$WORK/shop-cache" > "$WORK/c4.json"
node - "$WORK/c1.json" "$WORK/c2.json" "$WORK/c3.json" "$WORK/c4.json" "$WORK/shop2.json" "$WORK" <<'NODE'
const { load, fail } = require(process.argv[7] + '/lib.cjs');
const [c1, c2, c3, c4, fresh] = process.argv.slice(2, 7).map(load);
if (c2.metrics.projectsFromCache !== 5) fail('unchanged solution should be served from cache for all 5 projects, got ' + c2.metrics.projectsFromCache);
const body = g => JSON.stringify({ n: g.nodes, e: g.edges, d: g.dispatch, p: g.entryPoints });
if (body(c2) !== body(fresh)) fail('cached graph differs from a fresh graph');
if (c3.metrics.projectsFromCache !== 4) fail('editing the leaf project must re-extract only that project, cached ' + c3.metrics.projectsFromCache);
if (c4.metrics.projectsFromCache !== 0) fail('editing Domain (referenced by every project) must invalidate all fragments, cached ' + c4.metrics.projectsFromCache);
console.log('Cache verified: unchanged = 5/5 cached; leaf edit = 4/5; Domain edit invalidates dependents (0/5); cached graph equals fresh graph.');
NODE

# ---------------------------------------------------------------- 4. closed graph (positive) and incomplete references (negative)
engine graph build "$FIX/closed" > "$WORK/closed.json"
engine graph build "$FIX/incomplete" > "$WORK/incomplete.json"
engine graph cycles "$FIX/incomplete" > "$WORK/incomplete-cycles.json"
node - "$WORK/closed.json" "$WORK/incomplete.json" "$WORK/incomplete-cycles.json" "$WORK" <<'NODE'
const { load, fail, index } = require(process.argv[5] + '/lib.cjs');
const [closed, incomplete, cycles] = process.argv.slice(2, 5).map(load);
if (!closed.completeness.closed || closed.completeness.unresolvedCallSites !== 0) fail('a fully bound, internal-only graph should be reported as closed: ' + JSON.stringify(closed.completeness));
const ci = index(closed);
if (!ci.edge(ci.find('Calculator', 'AddTwice(int, int)'), ci.find('Calculator', 'Add(int, int)'), 'direct')) fail('closed fixture: AddTwice -> Add');
const ii = index(incomplete);
if (incomplete.completeness.closed || incomplete.completeness.unresolvedCallSites < 1) fail('missing references must make the graph incomplete: ' + JSON.stringify(incomplete.completeness));
const unresolved = incomplete.edges.filter(e => e.resolution === 'unresolved');
if (!unresolved.length || unresolved.some(e => e.to !== null || e.certainty !== 'unknown' || !e.reason)) fail('unresolved edges must have no target, certainty unknown and a reason');
if (!unresolved.some(e => (e.label || '').includes('JsonConvert'))) fail('unresolved edge should name the callee expression');
if (!incomplete.edges.some(e => e.from === ii.find('Worker', 'Known()').id && incomplete.nodes.find(n => n.id === e.to)?.isExternal)) fail('known BCL call should be an external node edge');
if (!cycles.notes.some(n => n.includes('not a proof'))) fail('cycle report must warn that absence of a cycle is not proof on an incomplete graph');
console.log('Completeness verified: closed fixture is closed; unresolved references are explicit edges and make the graph incomplete.');
NODE

# ASP.NET Core is a FrameworkReference (not in project.assets.json): a Web SDK project must still bind MapGet / Results.
engine graph build "$FIX/webapp" > "$WORK/webapp.json"
node - "$WORK/webapp.json" "$WORK" <<'NODE'
const { load, fail, index } = require(process.argv[3] + '/lib.cjs');
const g = load(process.argv[2]);
if (g.completeness.unresolvedCallSites !== 0) fail('ASP.NET Core calls should bind through the targeting pack: ' + g.edges.filter(e => e.resolution === 'unresolved').map(e => e.label));
if (!g.nodes.some(n => n.isExternal && n.containingType === 'Microsoft.AspNetCore.Http.Results' && n.name === 'Ok')) fail('Results.Ok should be an external node');
if (g.entryPoints.length !== 1 || g.entryPoints[0].label !== 'GET /ping') fail('minimal-API entry point');
console.log('Framework references verified: a Web SDK project binds MapGet/Results through the ASP.NET Core targeting pack.');
NODE

# ---------------------------------------------------------------- 5. cycles and architecture rules
engine graph build "$FIX/cycles" > "$WORK/cycles-graph.json"
engine architecture-check "$FIX/cycles" > "$WORK/cycles-arch.json"
engine architecture-check "$FIX/shop" > "$WORK/arch.json"
node - "$WORK/cycles-graph.json" "$WORK/cycles-arch.json" "$WORK/arch.json" "$WORK" <<'NODE'
const { load, fail } = require(process.argv[5] + '/lib.cjs');
const [cg, ca, arch] = process.argv.slice(2, 5).map(load);
if (!cg.warnings.some(w => w.includes('reference cycle'))) fail('a ProjectReference cycle must be reported when the graph is built');
const pc = ca.findings.filter(f => f.ruleId === 'ATL3002');
if (pc.length !== 1 || pc[0].validation !== 'staticProven' || pc[0].severity !== 'error' || !pc[0].evidence.every(e => e.location.file.endsWith('.csproj') && e.location.line > 1)) fail('project cycle finding needs csproj evidence with line numbers', pc);
if (JSON.stringify(ca.cycles.projectCycles[0].projects) !== '["Cyc.A","Cyc.B"]') fail('project cycle members');
if (!ca.findings.some(f => f.ruleId === 'ATL3003')) fail('configured noCycles(layers) must report the Alpha/Beta layer cycle');

const by = id => arch.findings.filter(f => f.ruleId === id);
if (arch.status !== 'configured') fail('status ' + arch.status);
const forbidden = by('ATL3010');
if (forbidden.length !== 2 || !forbidden.some(f => f.location.file.endsWith('.csproj')) || !forbidden.some(f => f.location.file === 'Application/OrderService.cs')) fail('Application -> Infrastructure must be reported once as a project reference and once as a call', forbidden.map(f => f.title));
if (!forbidden.every(f => f.validation === 'staticProven' && f.severity === 'error' && f.evidence.length && f.limitations.length && f.recommendation)) fail('architecture findings must carry evidence, limitations and a recommendation');
if (arch.findings.some(f => f.title.includes('domain-isolated'))) fail('false positive: Domain depends on nothing outward');
if (by('ATL3011').length || by('ATL3012').length || by('ATL3003').length) fail('exceptions/negatives produced findings: ' + by('ATL3011').map(f => f.title));
if (arch.exceptionsApplied.length !== 2 || !arch.exceptionsApplied.every(e => e.reason && e.occurrences >= 1)) fail('both justified exceptions should have been applied and reported: ' + JSON.stringify(arch.exceptionsApplied));
const cyc = by('ATL3001');
if (cyc.length !== 1 || cyc[0].validation !== 'staticProven' || !cyc[0].callPath || cyc[0].callPath.length < 3) fail('Alpha <-> Beta call cycle with a navigable path', cyc);
if (!arch.cycles.legitimateRecursion.some(c => c.kind === 'directRecursion') || !arch.cycles.legitimateRecursion.some(c => c.kind === 'mutualRecursion')) fail('recursion must be listed as legitimate');
if (arch.findings.some(f => f.title.includes('Factorial') || f.title.includes('IsEven'))) fail('legitimate recursion must not produce findings');
if (arch.cycles.callCycles.some(c => c.kind !== 'crossTypeCycle')) fail('unexpected possible cycles: ' + JSON.stringify(arch.cycles.callCycles.map(c => c.kind)));
console.log('Cycles/architecture verified: project cycle with csproj evidence, layer cycle, call cycle across types, recursion not reported, forbidden dependencies (reference + call), justified exceptions applied.');
NODE

# configuration handling: absent, invalid and unjustified exceptions
cp -R "$FIX/shop" "$WORK/shop-noconfig" && rm "$WORK/shop-noconfig/codeatlas.architecture.json"
engine architecture-check "$WORK/shop-noconfig" > "$WORK/noconfig.json"
cp -R "$FIX/shop" "$WORK/shop-badconfig"
cat > "$WORK/shop-badconfig/codeatlas.architecture.json" <<'JSON'
{ "layers": [ { "name": "Application", "projects": ["Shop.Application"] }, { "name": "Infrastructure", "projects": ["Shop.Infrastructure"] } ],
  "rules": [ { "id": "r1", "type": "forbidden", "from": "Application", "to": "Infrastructure", "severity": "warning" },
             { "id": "r2", "type": "forbidden", "from": "Nope", "to": "Application" }, { "id": "r3", "type": "bogus" } ],
  "exceptions": [ { "rule": "r1", "from": "Shop.Application*", "to": "Shop.Infrastructure*" } ] }
JSON
engine architecture-check "$WORK/shop-badconfig" > "$WORK/badconfig.json"
printf '{ not json' > "$WORK/shop-badconfig/codeatlas.architecture.json"
engine architecture-check "$WORK/shop-badconfig" > "$WORK/brokenconfig.json"
engine architecture-rules "$FIX/shop" > "$WORK/rules.json"
node - "$WORK/noconfig.json" "$WORK/badconfig.json" "$WORK/brokenconfig.json" "$WORK/rules.json" "$WORK" <<'NODE'
const { load, fail } = require(process.argv[6] + '/lib.cjs');
const [none, bad, broken, rules] = process.argv.slice(2, 6).map(load);
if (none.status !== 'notConfigured' || none.findings.some(f => ['ATL3010', 'ATL3011', 'ATL3012'].includes(f.ruleId))) fail('without a config no layer rule may run (no imposed convention)');
if (!none.findings.some(f => f.ruleId === 'ATL3001')) fail('convention-free call-cycle check should still run');
const rule = bad.findings.filter(f => f.ruleId === 'ATL3010');
if (rule.length !== 2 || rule.some(f => f.severity !== 'warning')) fail('configured severity must apply and an exception without a reason must be ignored', rule.map(f => f.title));
if (!bad.warnings.some(w => w.includes('no reason')) || !bad.warnings.some(w => w.includes('undefined layer')) || !bad.warnings.some(w => w.includes('unknown type'))) fail('config problems must be reported: ' + bad.warnings);
if (broken.status !== 'invalidConfiguration' || broken.findings.some(f => f.ruleId === 'ATL3010')) fail('an unreadable config must not run rules or pretend');
if (rules.layers.length !== 4 || rules.rules.length !== 5 || rules.exceptions.length !== 2) fail('architecture-rules should echo the parsed configuration');
console.log('Architecture configuration verified: no config = no layer rules, invalid entries reported, exceptions need reasons, severity configurable.');
NODE

# ---------------------------------------------------------------- 6. impact analysis
impact() { engine impact "$FIX/shop" "$@"; }
impact "OrderService.Get" > "$WORK/i-get.json"
impact "IOrderRepository.Find" > "$WORK/i-iface.json"
impact "SqlOrderRepository.Find" > "$WORK/i-impl.json"
impact "Application/OrderService.cs:11" > "$WORK/i-pos.json"
CODE=0; impact "Calculator.Compute" > "$WORK/i-amb.json" || CODE=$?; [[ "$CODE" == "6" ]] || fail "ambiguous selector must exit 6, got $CODE"
CODE=0; impact "Nope.Missing" > "$WORK/i-none.json" || CODE=$?; [[ "$CODE" == "3" ]] || fail "unknown selector must exit 3, got $CODE"
SELECTED="$(node -e 'const r=JSON.parse(require("fs").readFileSync(process.argv[1]));console.log(r.matches.find(m=>m.symbol.includes("Billing")).id)' "$WORK/i-amb.json")"
impact "$SELECTED" > "$WORK/i-id.json"
impact "IOrderRepository.Find" --depth 1 > "$WORK/i-depth.json"
engine impact "$FIX/closed" "Calculator.Add" > "$WORK/i-closed.json"
node - "$WORK" <<'NODE'
const w = process.argv[2];
const { load, fail } = require(w + '/lib.cjs');
const L = n => load(`${w}/${n}.json`);
const get = L('i-get'), iface = L('i-iface'), impl = L('i-impl'), pos = L('i-pos'), amb = L('i-amb'), none = L('i-none'), byId = L('i-id'), depth = L('i-depth'), closed = L('i-closed');
const names = items => items.map(i => `${i.node.symbol}[${i.certainty}]`).sort();
const has = (items, text, certainty) => items.some(i => i.node.symbol.includes(text) && (!certainty || i.certainty === certainty));

if (!has(get.directReferences, 'OrdersController.Get', 'proven') || !has(get.directReferences, 'OrderServiceTests.Get_returns_order', 'proven')) fail('known direct consumers of OrderService.Get', names(get.directReferences));
if (!has(get.relatedTests, 'Get_returns_order', 'proven')) fail('related tests');
if (!get.businessFlows.some(f => f.label === 'GET /api/orders/{id}' && f.certainty === 'proven')) fail('business flow through OrderService.Get');
if (JSON.stringify(get.projectsAffected.map(p => p.project)) !== '["Shop.Application","Shop.Tests","Shop.Web"]') fail('projects affected');
if (!get.layersAffected.includes('Presentation')) fail('layers affected');
if (get.exhaustive || !get.statement.startsWith('NOT exhaustive')) fail('a public member of an open-world solution must not be reported as exhaustive');

// changing a contract: callers through the interface are proven, callers of an implementation only potential
if (!has(iface.directReferences, 'OrderService.Get', 'proven')) fail('interface member consumer');
if (iface.implementationsAffected.length !== 2) fail('implementations of IOrderRepository.Find: ' + iface.implementationsAffected.length);
if (!iface.contractRisks.some(r => r.kind === 'implementations')) fail('implementations risk');
if (!has(impl.directReferences, 'OrderService.Legacy', 'proven') || !has(impl.directReferences, 'OrderService.Get', 'potential')) fail('implementation consumers must separate proven from potential', names(impl.directReferences));
if (!impl.contractsImplemented.some(c => c.symbol.includes('IOrderRepository.Find')) || !impl.contractRisks.some(r => r.kind === 'implementsContract')) fail('implemented contract');
if (!impl.businessFlows.some(f => f.label === 'GET /api/orders/legacy/{id}' && f.certainty === 'proven') || !impl.businessFlows.some(f => f.label === 'GET /api/orders/{id}' && f.certainty === 'potential')) fail('flow certainty through dispatch');
if (!impl.indirectConsumers.length || !impl.indirectConsumers.every(i => i.path && i.path.length >= 2)) fail('indirect consumers need a path');

// selectors
if (pos.status !== 'ok' || !pos.symbol.symbol.includes('OrderService.Place')) fail('position selector: ' + pos.symbol?.symbol);
if (amb.status !== 'ambiguous' || amb.matches.length !== 2 || amb.symbol !== null) fail('ambiguous selector must return the matches and no symbol');
if (none.status !== 'notFound') fail('not found');
if (byId.status !== 'ok' || !byId.symbol.symbol.includes('Billing.Calculator.Compute')) fail('selection by stable id');

// limits are reported, not silent
if (!depth.unknowns.depthLimited || !depth.statement.includes('depth')) fail('depth limit must be reported');

// exhaustive only when the graph is closed and nothing escapes the solution
if (!closed.exhaustive || closed.statement.startsWith('NOT exhaustive') || !has(closed.directReferences, 'AddTwice', 'proven')) fail('closed internal graph should be exhaustive', closed.statement);
console.log('Impact verified: proven vs potential consumers, implementations, contracts, tests, flows, projects/layers; ambiguity and not-found handled; not exhaustive unless the graph is closed.');
NODE

# ---------------------------------------------------------------- 7. interprocedural rules (positive and negative)
engine graph analyze "$FIX/interproc" > "$WORK/ip.json"
node - "$WORK/ip.json" "$WORK" <<'NODE'
const { load, fail } = require(process.argv[3] + '/lib.cjs');
const r = load(process.argv[2]);
const expected = [
  ['ATL3101', 'HandleAsync', 'staticProven', ['HandleAsync', 'Helper', 'Fetch', 'GetResult']],
  ['ATL3102', 'LoopOverUrls', 'staticRisk', ['LoopOverUrls', 'Load', 'Fetch', 'GetStringAsync']],
  ['ATL3103', 'WithToken', 'staticProven', ['WithToken', 'Middle', 'FetchNoToken', 'GetStringAsync']],
  ['ATL3104', 'Leak', 'staticRisk', ['Leak', 'Open']],
  ['ATL3104', 'Discard', 'staticRisk', ['Discard', 'Open']],
  ['ATL3105', 'UseUnchecked', 'staticRisk', ['UseUnchecked', 'Find']],
];
for (const [rule, member, validation, path] of expected) {
  const hits = r.findings.filter(f => f.ruleId === rule && f.location.symbol.includes('.' + member + '('));
  if (hits.length !== 1) fail(`${rule} should report exactly one finding for ${member}, got ${hits.length}`);
  const f = hits[0];
  if (f.validation !== validation) fail(`${rule}/${member} must be ${validation}, got ${f.validation}`);
  const steps = f.callPath.map(s => s.symbol);
  path.forEach((p, i) => { if (!(steps[i] || '').includes(p)) fail(`${rule}/${member}: path step ${i} should contain ${p}: ${steps.join(' -> ')}`); });
  if (!f.evidence.length || !f.limitations.length || !f.recommendation || !f.reproduction || f.origin !== 'static' || !/^F-[0-9a-f]{12}$/.test(f.id)) fail(`${rule}/${member}: incomplete finding`);
}
if (r.findings.length !== expected.length) fail('false positives: ' + r.findings.filter(f => !expected.some(e => e[0] === f.ruleId && f.location.symbol.includes('.' + e[1] + '('))).map(f => f.ruleId + ' ' + f.title));
if (!r.notes.some(n => n.includes('depth'))) fail('analysis limits must be stated');
console.log(`Interprocedural verified: ${expected.length} cross-method findings with call paths; HandleAsyncOk, LoopPure, WithTokenOk, Fine and UseChecked stay silent.`);
NODE

# ---------------------------------------------------------------- 8. limits: dispatch fan-out is truncated explicitly
engine graph build "$FIX/shop" --no-cache --max-dispatch-targets 1 > "$WORK/limited.json"
node - "$WORK/limited.json" "$WORK" <<'NODE'
const { load, fail, index } = require(process.argv[3] + '/lib.cjs');
const g = load(process.argv[2]), { find, out } = index(g);
if (!g.limitsApplied.some(l => l.limit === 'maxDispatchTargets' && l.value === 1)) fail('the truncation must be recorded in limitsApplied');
if (g.completeness.truncatedDispatchSites < 1 || g.completeness.closed) fail('truncation must be reflected in completeness');
const area = out(find('OrderService', 'OneArea(IShape)')).filter(e => e.resolution === 'interfaceDispatch');
if (area.filter(e => e.certainty === 'potential').length !== 1 || !area.every(e => e.truncated)) fail('only one target may be kept and every edge of the site must be marked truncated');
console.log('Limits verified: fan-out above the limit is cut, marked on each edge, listed in limitsApplied and counted in completeness.');
NODE

# ---------------------------------------------------------------- 9. shared business flows vs the legacy tracer
engine scan "$ROOT/tests/fixtures/architecture" --no-persist > "$WORK/scan.json"
engine graph flows "$ROOT/tests/fixtures/architecture" > "$WORK/flows.json"
node - "$WORK/scan.json" "$WORK/flows.json" "$WORK" <<'NODE'
const { load, fail } = require(process.argv[4] + '/lib.cjs');
const scan = load(process.argv[2]), flows = load(process.argv[3]);
if (flows.source !== 'callGraph') fail('flows must declare their source');
const traced = scan.businessFlows.filter(f => f.steps.length > 1);
if (traced.length < 2) fail('fixture should provide traced legacy flows');
const bySymbol = (flow, symbol) => flow.steps.some(s => s.symbol === symbol || s.symbol.endsWith('.' + symbol.split('.').pop()) && s.symbol.startsWith(symbol.split('.')[0]));
let compared = 0;
for (const legacy of traced) {
  const shared = flows.flows.find(f => f.name === legacy.name && f.projectName === legacy.projectName);
  if (!shared) fail('no shared flow for ' + legacy.name);
  for (const step of legacy.steps.slice(1)) {
    // Name-based publish detection is deliberately not reproduced by the graph (it infers from the method name).
    if (step.kind === 'message-publish') continue;
    if (!bySymbol(shared, step.symbol)) fail(`shared flow "${legacy.name}" lost legacy step ${step.kind} ${step.symbol}: ${shared.steps.map(s => s.symbol).join(' | ')}`);
    compared++;
  }
}
const post = flows.flows.find(f => f.name === 'POST /api/cargo');
if (!post.steps.some(s => s.kind === 'request-handler' && s.symbol.includes('RegisterCargoCommandHandler.Handle') && s.confidence === 'candidate')) fail('mediator handler must be linked as a candidate, never as proven');
if (!flows.flows.some(f => f.steps.some(s => s.kind === 'unresolved-call'))) fail('unresolved calls must appear as explicit steps');
if (flows.graphCompleteness.closed) fail('flows over an incomplete graph must say so');
console.log(`Shared flows verified: all ${compared} resolvable legacy steps are present in the graph projection; inferred links are candidates; unresolved calls are explicit.`);
NODE

# ---------------------------------------------------------------- 10. cancellation during graph construction
BIG="$WORK/big"; mkdir -p "$BIG"
# 400 projects take ~10 s to build here; a signal at 5 s lands mid-build (loading or extraction).
for i in $(seq 1 400); do mkdir -p "$BIG/P$i"; cp "$FIX/closed/Closed.csproj" "$BIG/P$i/P$i.csproj"; cp "$FIX/closed/Closed.cs" "$BIG/P$i/Closed.cs"; done
dotnet "$ENGINE" graph build "$BIG" --no-cache > "$WORK/cancel.json" &
PID=$!
sleep 5
kill -TERM "$PID"
CODE=0; wait "$PID" || CODE=$?
[[ "$CODE" == "130" ]] || fail "expected exit 130 after SIGTERM during graph construction, got $CODE (0 means the workload finished before the signal)"
node -e '
const g = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
if (g.status !== "cancelled" || g.completeness.closed || !g.warnings.some(w => w.includes("cancelled"))) { console.error("Call graph test failed: cancelled build must be marked cancelled and not complete"); process.exit(1); }
if (g.metrics.projects >= 400) { console.error("Call graph test failed: expected a partial graph"); process.exit(1); }
console.log("Cancellation verified: SIGTERM mid-build produced a partial graph (" + g.metrics.projects + "/400 projects) marked cancelled and not complete, exit 130.");' "$WORK/cancel.json"

# Cancellation during extraction (not just loading): four heavy projects built one at a time.
HEAVY="$WORK/heavy"
for p in 1 2 3 4; do
  mkdir -p "$HEAVY/H$p"; cp "$FIX/closed/Closed.csproj" "$HEAVY/H$p/H$p.csproj"
  python3 - "$HEAVY/H$p/Big.cs" <<'PY'
import sys
lines = ["namespace Big;", "public class Heavy {"]
for i in range(3000):
    lines.append(f"  public int M{i}(int a) {{ var x = a + {i}; return M{(i + 1) % 3000}(x) + M{(i + 7) % 3000}(x) + System.Math.Abs(x) + x.ToString().Length; }}")
lines.append("}")
open(sys.argv[1], "w").write("\n".join(lines))
PY
done
dotnet "$ENGINE" graph build "$HEAVY" --no-cache --max-parallelism 1 > "$WORK/cancel2.json" &
PID=$!
sleep 7
kill -TERM "$PID"
CODE=0; wait "$PID" || CODE=$?
[[ "$CODE" == "130" ]] || fail "expected exit 130 after SIGTERM during extraction, got $CODE (0 means the machine finished all four projects first)"
node -e '
const g = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
if (g.status !== "cancelled" || g.completeness.closed) { console.error("Call graph test failed: cancelled extraction must be marked cancelled and not complete"); process.exit(1); }
if (g.metrics.projects >= 4) { console.error("Call graph test failed: expected a partial graph"); process.exit(1); }
console.log("Cancellation during extraction verified: " + g.metrics.projects + "/4 projects extracted, graph marked cancelled and not complete, exit 130.");' "$WORK/cancel2.json"

echo "Call graph test passed."
