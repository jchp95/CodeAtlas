#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FIXTURE="$ROOT/tests/fixtures/architecture"
PROJECT="$ROOT/engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj"
OUTPUT="$(mktemp "${TMPDIR:-/tmp}/codeatlas-http-catalog.XXXXXX")"
trap 'rm -f "$OUTPUT"' EXIT
DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --verbosity quiet --project "$PROJECT" -- http-catalog "$FIXTURE" > "$OUTPUT"
node - "$OUTPUT" <<'NODE'
const fs = require('node:fs');
const catalog = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const failures = [];
const post = catalog.endpoints.find(e => e.projectName === 'ZasExpress.Api' && e.method === 'POST' && e.route === '/api/cargo');
if (!post) failures.push('missing POST /api/cargo in endpoint catalog');
if (post && post.schemaSource !== 'source-type') failures.push(`expected source-derived request schema, got ${post.schemaSource}`);
if (post && (!post.sampleBodyJson || !JSON.parse(post.sampleBodyJson).cargoId)) failures.push('POST /api/cargo did not produce a request-body example from RegisterCargoRequest');
const tracking = catalog.endpoints.find(e => e.projectName === 'ZasTrack.Api' && e.method === 'GET' && e.route === '/api/tracking');
if (!tracking) failures.push('missing controller route GET /api/tracking with class-level [Route] prefix');
const savedPost = catalog.requests.find(r => r.sourcePath.endsWith('zas-express-api/tests/cargo.http') && r.method === 'POST');
if (!savedPost) failures.push('missing POST request parsed from cargo.http');
if (savedPost && (!post || !savedPost.associatedEndpointIds.includes(post.id))) failures.push('cargo.http POST was not associated with POST /api/cargo');
const health = catalog.requests.find(r => r.sourcePath.endsWith('zas-express-api/tests/cargo.http') && r.method === 'GET' && r.url.includes('/api/health'));
if (health && !catalog.endpoints.some(e => health.associatedEndpointIds.includes(e.id) && e.route === '/api/health')) failures.push('health request was not associated with GET /api/health');
const trackingTest = catalog.requests.find(r => r.sourcePath.endsWith('zas-express-api/tests/tracking.http'));
if (!trackingTest) failures.push('missing independent tracking.http request');
if (trackingTest && !trackingTest.availableEnvironments.includes('local')) failures.push('did not discover Rider local environment in http-client.env.json');
if (trackingTest && tracking && !trackingTest.associatedEndpointIds.includes(tracking.id)) failures.push('variable-prefixed tracking URL was not matched to /api/tracking');
const csharpCall = (catalog.httpCalls || []).find(c => c.sourceProjectName === 'ZasExpress.Api' && c.sourcePath.endsWith('Services/TrackingClient.cs') && c.method === 'GET' && c.route === '/api/tracking');
if (!csharpCall) failures.push('typed .NET HttpClient.GetAsync call in TrackingClient.cs was not discovered');
if (csharpCall && tracking && !csharpCall.targetEndpointIds.includes(tracking.id)) failures.push('typed .NET call was not linked to the ZasTrack receiver endpoint');
const tsCall = (catalog.httpCalls || []).find(c => c.sourceProjectName === 'zas-operations-ui' && c.method === 'GET' && c.route === '/api/tracking');
if (!tsCall) failures.push('TypeScript fetch with a local constant base URL was not discovered');
if (tsCall && tracking && !tsCall.targetEndpointIds.includes(tracking.id)) failures.push('TypeScript fetch was not linked to the ZasTrack receiver endpoint');
if (failures.length) {
  console.error('HTTP catalog test failed:\n- ' + failures.join('\n- '));
  console.error('Discovered endpoints:', JSON.stringify(catalog.endpoints.map(e => ({ projectName:e.projectName, method:e.method, route:e.route, bodyType:e.requestType, schemaSource:e.schemaSource })), null, 2));
  console.error('Discovered requests:', JSON.stringify(catalog.requests.map(r => ({ file:r.sourcePath, method:r.method, url:r.url, associated:r.associatedEndpointIds, envs:r.availableEnvironments })), null, 2));
  console.error('Discovered HTTP calls:', JSON.stringify(catalog.httpCalls || [], null, 2));
  process.exit(1);
}
console.log(`HTTP catalog test passed: ${catalog.endpoints.length} endpoints, ${catalog.requests.length} .http requests, ${(catalog.httpCalls || []).length} outgoing HTTP calls; route/schema/association/trace/environment discovery verified.`);
NODE
