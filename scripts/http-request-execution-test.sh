#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj"
DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet build --verbosity quiet "$PROJECT"
node - "$PROJECT" <<'NODE'
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const { spawn } = require('node:child_process');
const project = process.argv[2];
const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'codeatlas-http-run-'));
const json = value => Buffer.from(JSON.stringify(value)).toString('base64');
const base64 = value => Buffer.from(value).toString('base64');
const run = (args) => new Promise((resolve, reject) => {
  const child = spawn('dotnet', ['run', '--no-build', '--verbosity', 'quiet', '--project', project, '--', ...args], { encoding: 'utf8' });
  let stdout = '', stderr = '';
  child.stdout.on('data', chunk => stdout += chunk);
  child.stderr.on('data', chunk => stderr += chunk);
  child.on('error', reject);
  child.on('close', code => code === 0 ? resolve(stdout) : reject(new Error(`dotnet exited ${code}: ${stderr}\n${stdout}`)));
});

const server = http.createServer((request, response) => {
  let body = '';
  request.setEncoding('utf8');
  request.on('data', chunk => body += chunk);
  request.on('end', () => {
    response.writeHead(200, { 'content-type': 'application/json', 'x-codeatlas-test': 'ok' });
    response.end(JSON.stringify({ method: request.method, path: request.url, body }));
  });
});

server.listen(0, '127.0.0.1', async () => {
  try {
    const address = server.address();
    const baseUrl = `http://127.0.0.1:${address.port}`;
    fs.mkdirSync(path.join(tempRoot, 'nested'), { recursive: true });
    // Parent values act as defaults; repository-local public and private files should override them.
    fs.writeFileSync(path.join(tempRoot, 'http-client.env.json'), JSON.stringify({ local: { baseUrl: 'http://127.0.0.1:1' } }, null, 2));
    fs.writeFileSync(path.join(tempRoot, 'nested', 'http-client.env.json'), JSON.stringify({ local: { baseUrl: 'http://127.0.0.1:2' } }, null, 2));
    fs.writeFileSync(path.join(tempRoot, 'nested', 'http-client.private.env.json'), JSON.stringify({ local: { baseUrl } }, null, 2));
    fs.writeFileSync(path.join(tempRoot, 'nested', 'sample.http'), [
      '### POST echo',
      'POST {{baseUrl}}/echo',
      'Content-Type: application/json',
      'Accept: application/json',
      '',
      '{"source":"http-file"}',
      ''
    ].join('\n'));

    const directOutput = await run(['http-call', 'POST', `${baseUrl}/direct`, base64('Content-Type: application/json'), base64('{"source":"direct"}')]);
    const direct = JSON.parse(directOutput);
    if (!direct.success || direct.statusCode !== 200 || !JSON.parse(direct.body).body.includes('direct')) {
      throw new Error('Direct HTTP execution did not return the expected status/body: ' + directOutput);
    }

    const override = json({ method: 'POST', url: '{{baseUrl}}/echo', headersText: 'Content-Type: application/json\nAccept: application/json', body: '{"source":"http-file"}' });
    const fileOutput = await run(['http-file-run', tempRoot, 'nested/sample.http', '1', 'local', override]);
    const fileResult = JSON.parse(fileOutput);
    if (!fileResult.success || fileResult.statusCode !== 200 || !JSON.parse(fileResult.body).body.includes('http-file')) {
      throw new Error('Rider-style .http execution did not resolve environment variables or return expected body: ' + fileOutput);
    }
    console.log('HTTP request execution test passed: direct request, .http parsing, environment precedence/private overrides, substitution and response capture.');
  } catch (error) {
    console.error('HTTP request execution test failed:', error.message);
    process.exitCode = 1;
  } finally {
    server.close(() => {});
    fs.rmSync(tempRoot, { recursive: true, force: true });
  }
});
NODE
