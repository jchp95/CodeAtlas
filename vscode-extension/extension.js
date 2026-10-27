'use strict';

const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');
const http = require('http');

const CONFIG_PATH = path.join(os.homedir(), 'Library', 'Application Support', 'CodeAtlas', 'vscode-bridge.json');
let statusItem;

function readConfig() {
  let config;
  try {
    config = JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8'));
  } catch (error) {
    throw new Error(`No se encontró la configuración local de CodeAtlas. Abre primero CodeAtlas en macOS. Ruta esperada: ${CONFIG_PATH}`);
  }
  if (!Number.isInteger(config.port) || config.port < 1 || config.port > 65535 || typeof config.token !== 'string' || config.token.length < 48) {
    throw new Error('La configuración del puente CodeAtlas no es válida. Reinicia CodeAtlas para regenerarla.');
  }
  return config;
}

function bridgeRequest(method, route, payload) {
  const config = readConfig();
  const body = payload === undefined ? Buffer.alloc(0) : Buffer.from(JSON.stringify(payload), 'utf8');
  return new Promise((resolve, reject) => {
    const request = http.request({
      hostname: '127.0.0.1',
      port: vscode.workspace.getConfiguration('codeAtlas').get('bridgePort', config.port),
      path: route,
      method,
      timeout: 2500,
      headers: {
        'X-CodeAtlas-Token': config.token,
        'Accept': 'application/json',
        'Content-Type': 'application/json; charset=utf-8',
        'Content-Length': body.length,
        'Connection': 'close'
      }
    }, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => {
        const text = Buffer.concat(chunks).toString('utf8');
        let data = {};
        try { data = text ? JSON.parse(text) : {}; } catch (_) { /* server error will be reported by status */ }
        if ((response.statusCode || 500) >= 200 && (response.statusCode || 500) < 300) resolve(data);
        else reject(new Error(data.error || `CodeAtlas respondió HTTP ${response.statusCode || 500}`));
      });
    });
    request.on('timeout', () => request.destroy(new Error('Tiempo de espera agotado. Comprueba que CodeAtlas esté abierto.')));
    request.on('error', error => reject(new Error(`No se pudo conectar con CodeAtlas en 127.0.0.1:${config.port}. ${error.message}`)));
    if (body.length) request.write(body);
    request.end();
  });
}

async function checkConnection(showMessage) {
  try {
    const result = await bridgeRequest('GET', '/v1/status');
    if (statusItem) {
      statusItem.text = '$(plug) CodeAtlas';
      statusItem.tooltip = `Conectado · ${result.version || 'CodeAtlas'}`;
      statusItem.backgroundColor = undefined;
    }
    if (showMessage) vscode.window.showInformationMessage('Conexión con CodeAtlas correcta.');
    return true;
  } catch (error) {
    if (statusItem) {
      statusItem.text = '$(debug-disconnect) CodeAtlas';
      statusItem.tooltip = error.message;
      statusItem.backgroundColor = new vscode.ThemeColor('statusBarItem.warningBackground');
    }
    if (showMessage) vscode.window.showWarningMessage(error.message);
    return false;
  }
}

async function openActiveFile() {
  const editor = vscode.window.activeTextEditor;
  if (!editor || editor.document.uri.scheme !== 'file') {
    vscode.window.showInformationMessage('Abre primero un archivo local en Visual Studio Code.');
    return;
  }
  const workspace = vscode.workspace.getWorkspaceFolder(editor.document.uri);
  try {
    await bridgeRequest('POST', '/v1/open', {
      path: editor.document.uri.fsPath,
      workspaceRoot: workspace ? workspace.uri.fsPath : path.dirname(editor.document.uri.fsPath),
      line: editor.selection.active.line + 1
    });
    vscode.window.showInformationMessage(`Abriendo ${path.basename(editor.document.uri.fsPath)} en CodeAtlas.`);
  } catch (error) {
    vscode.window.showErrorMessage(error.message);
  }
}

async function openWorkspace() {
  const folders = vscode.workspace.workspaceFolders || [];
  if (!folders.length) {
    vscode.window.showInformationMessage('Abre primero una carpeta o workspace en Visual Studio Code.');
    return;
  }
  const selected = folders.length === 1
    ? folders[0]
    : await vscode.window.showQuickPick(folders.map(folder => ({ label: folder.name, description: folder.uri.fsPath, folder })), { placeHolder: 'Selecciona el workspace que abrirás en CodeAtlas' });
  if (!selected) return;
  const folder = selected.folder || selected;
  try {
    await bridgeRequest('POST', '/v1/open-workspace', { path: folder.uri.fsPath });
    vscode.window.showInformationMessage(`Workspace enviado a CodeAtlas: ${folder.name}`);
  } catch (error) {
    vscode.window.showErrorMessage(error.message);
  }
}

function activate(context) {
  statusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 40);
  statusItem.command = 'codeAtlas.checkConnection';
  context.subscriptions.push(statusItem);
  statusItem.show();
  context.subscriptions.push(vscode.commands.registerCommand('codeAtlas.openActiveFile', openActiveFile));
  context.subscriptions.push(vscode.commands.registerCommand('codeAtlas.openWorkspace', openWorkspace));
  context.subscriptions.push(vscode.commands.registerCommand('codeAtlas.checkConnection', () => checkConnection(true)));
  context.subscriptions.push(vscode.workspace.onDidChangeWorkspaceFolders(() => checkConnection(false)));
  checkConnection(false);
}

function deactivate() {
  if (statusItem) statusItem.dispose();
}

module.exports = { activate, deactivate };
