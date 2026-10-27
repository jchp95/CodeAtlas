#!/usr/bin/env node
'use strict';

const targetUri = process.argv[2];
let incoming = Buffer.alloc(0);
let configReady = false;
let queuedCompletionID = null;

function send(message) {
  const body = Buffer.from(JSON.stringify(message), 'utf8');
  const header = Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, 'ascii');
  process.stdout.write(Buffer.concat([header, body]));
}

function processMessage(message) {
  if (message.id !== undefined && message.method === 'initialize') {
    send({ jsonrpc: '2.0', id: message.id, result: { capabilities: { completionProvider: {}, definitionProvider: true, textDocumentSync: 1 } } });
  } else if (message.method === 'initialized') {
    // Exercise the server-to-client request path. Completion won't respond until this is answered.
    send({ jsonrpc: '2.0', id: 'server-config-1', method: 'workspace/configuration', params: { items: [{ section: 'one' }, { section: 'two' }] } });
  } else if (message.id === 'server-config-1') {
    configReady = Array.isArray(message.result) && message.result.length === 2;
    if (configReady) send({ jsonrpc: '2.0', id: 'server-apply-edit', method: 'workspace/applyEdit', params: { label: 'test edit', edit: { changes: {} } } });
  } else if (message.id === 'server-apply-edit') {
    if (message.result && message.result.applied === false) {
      send({ jsonrpc: '2.0', id: 'server-execute', method: 'workspace/executeCommand', params: { command: 'test.must.not.run' } });
    } else {
      process.exit(22);
    }
  } else if (message.id === 'server-execute') {
    if (Object.prototype.hasOwnProperty.call(message, 'result')) {
      send({ jsonrpc: '2.0', method: 'textDocument/publishDiagnostics', params: {
        uri: 'file://' + process.cwd() + '/Open.cs',
        diagnostics: [{ message: 'fake diagnostic', severity: 1, source: 'fake-lsp', code: 'FAKE1', range: { start: { line: 0, character: 0 }, end: { line: 0, character: 3 } } }]
      } });
    }
  } else if (message.id !== undefined && message.method === 'textDocument/completion') {
    if (configReady) respondCompletion(message.id);
    else queuedCompletionID = message.id;
  } else if (message.id !== undefined && message.method === 'textDocument/definition') {
    send({ jsonrpc: '2.0', id: message.id, result: { uri: targetUri, range: { start: { line: 3, character: 2 }, end: { line: 3, character: 5 } } } });
  } else if (message.id !== undefined && message.method === 'shutdown') {
    send({ jsonrpc: '2.0', id: message.id, result: null });
  } else if (message.method === 'exit') {
    process.exit(0);
  }
}

function respondCompletion(id) {
  send({ jsonrpc: '2.0', id, result: { isIncomplete: false, items: [{ label: 'FakeCompletion', detail: 'from test server', insertText: 'FakeCompletion', kind: 2 }] } });
  queuedCompletionID = null;
}

process.stdin.on('data', (chunk) => {
  incoming = Buffer.concat([incoming, chunk]);
  while (true) {
    const headerEnd = incoming.indexOf('\r\n\r\n');
    if (headerEnd < 0) return;
    const header = incoming.subarray(0, headerEnd).toString('ascii');
    const match = header.match(/(?:^|\r\n)Content-Length:\s*(\d+)/i);
    if (!match) process.exit(21);
    const length = Number(match[1]);
    const bodyStart = headerEnd + 4;
    if (incoming.length < bodyStart + length) return;
    const body = incoming.subarray(bodyStart, bodyStart + length);
    incoming = incoming.subarray(bodyStart + length);
    processMessage(JSON.parse(body.toString('utf8')));
  }
});
