#!/usr/bin/env node
'use strict';

// Optional TypeScript Compiler API pass. It resolves source imports only when a
// repository already has TypeScript installed; it never runs npm/npx or installs packages.
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

const root = path.resolve(process.argv[2] || '.');
const excluded = new Set(['.git', 'node_modules', '.next', 'dist', 'build', 'coverage', '.turbo', '.vercel', 'bin', 'obj']);
const maxPackages = 150;
const maxTsConfigs = 100;

function walk(dir, predicate, results, limit = 10000) {
  if (results.length >= limit) return;
  let entries;
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
  for (const entry of entries) {
    if (results.length >= limit) return;
    const absolute = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (excluded.has(entry.name) || entry.name.startsWith('.')) continue;
      walk(absolute, predicate, results, limit);
    } else if (entry.isFile() && predicate(absolute)) {
      results.push(absolute);
    }
  }
}

function safeReadJson(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return null; }
}

function relative(file) {
  return path.relative(root, file).split(path.sep).join('/');
}

function realPath(file) {
  try { return fs.realpathSync.native(file); } catch { return path.resolve(file); }
}

function inside(file, dir) {
  const rel = path.relative(dir, file);
  return rel === '' || (!rel.startsWith('..' + path.sep) && rel !== '..' && !path.isAbsolute(rel));
}

function globalTypeScriptRoots() {
  const roots = new Set();
  for (const entry of (process.env.NODE_PATH || '').split(path.delimiter).filter(Boolean)) roots.add(path.resolve(entry));
  for (const candidate of [
    process.env.npm_config_prefix ? path.join(process.env.npm_config_prefix, 'lib', 'node_modules') : null,
    '/opt/homebrew/lib/node_modules',
    '/usr/local/lib/node_modules',
    '/usr/lib/node_modules',
    path.join(path.dirname(process.execPath), '..', 'lib', 'node_modules'),
  ]) {
    if (candidate) roots.add(path.resolve(candidate));
  }
  try {
    const npmRoot = execFileSync('npm', ['root', '-g'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'], timeout: 1500 }).trim();
    if (npmRoot) roots.add(path.resolve(npmRoot));
  } catch { /* npm is optional; local resolution still works. */ }
  return [...roots];
}

const globalCompilerRoots = globalTypeScriptRoots();

function loadTypeScript(configRoot, repoRoot) {
  const searchPaths = [configRoot, repoRoot, ...globalCompilerRoots];
  try {
    return require(require.resolve('typescript', { paths: searchPaths }));
  } catch { /* Check common global installations directly below. */ }
  for (const rootPath of globalCompilerRoots) {
    for (const candidate of [path.join(rootPath, 'typescript'), path.join(rootPath, 'typescript', 'lib', 'typescript.js')]) {
      try { return require(candidate); } catch { /* Try the next candidate. */ }
    }
  }
  return null;
}

function main() {
  const packageFiles = [];
  walk(root, file => path.basename(file) === 'package.json', packageFiles, 3000);
  const packages = packageFiles.slice(0, maxPackages).map(file => {
    const packageRoot = path.dirname(file);
    const json = safeReadJson(file) || {};
    return { file, packageRoot, realRoot: realPath(packageRoot), name: typeof json.name === 'string' ? json.name : path.basename(packageRoot) };
  }).sort((a, b) => b.realRoot.length - a.realRoot.length);

  const tsconfigs = [];
  walk(root, file => path.basename(file) === 'tsconfig.json', tsconfigs, maxTsConfigs + 1);
  const relations = new Map();
  let compilerAvailable = false;
  let analyzedProjects = 0;
  const diagnostics = [];

  for (const configPath of tsconfigs.slice(0, maxTsConfigs)) {
    const configRoot = path.dirname(configPath);
    const ts = loadTypeScript(configRoot, root);
    if (!ts) continue;
    compilerAvailable = true;

    try {
      const configRead = ts.readConfigFile(configPath, ts.sys.readFile);
      if (configRead.error) continue;
      const parsed = ts.parseJsonConfigFileContent(configRead.config, ts.sys, configRoot, undefined, configPath);
      const program = ts.createProgram({ rootNames: parsed.fileNames, options: parsed.options });
      analyzedProjects++;
      const compilerOptions = program.getCompilerOptions();

      for (const sourceFile of program.getSourceFiles()) {
        const realSource = realPath(sourceFile.fileName);
        if (!inside(realSource, realPath(configRoot))) continue;
        if (sourceFile.isDeclarationFile || sourceFile.fileName.includes(`${path.sep}node_modules${path.sep}`)) continue;
        const sourcePackage = packages.find(p => inside(realSource, p.realRoot));
        if (!sourcePackage) continue;

        const imports = [];
        function visit(node) {
          if (ts.isImportDeclaration(node) && ts.isStringLiteralLike(node.moduleSpecifier)) {
            imports.push({ specifier: node.moduleSpecifier.text, node });
          } else if (ts.isExportDeclaration(node) && node.moduleSpecifier && ts.isStringLiteralLike(node.moduleSpecifier)) {
            imports.push({ specifier: node.moduleSpecifier.text, node });
          } else if (ts.isCallExpression(node) && node.arguments.length > 0) {
            const first = node.arguments[0];
            const isDynamicImport = node.expression.kind === ts.SyntaxKind.ImportKeyword;
            const isRequire = ts.isIdentifier(node.expression) && node.expression.text === 'require';
            if ((isDynamicImport || isRequire) && ts.isStringLiteralLike(first)) imports.push({ specifier: first.text, node });
          }
          ts.forEachChild(node, visit);
        }
        visit(sourceFile);

        for (const item of imports) {
          const resolution = ts.resolveModuleName(item.specifier, sourceFile.fileName, compilerOptions, ts.sys).resolvedModule;
          if (!resolution || !resolution.resolvedFileName) continue;
          const targetFile = realPath(resolution.resolvedFileName);
          if (targetFile.split(path.sep).includes('node_modules')) continue;
          const targetPackage = packages.find(p => inside(targetFile, p.realRoot));
          if (!targetPackage || targetPackage.file === sourcePackage.file) continue;
          const pos = sourceFile.getLineAndCharacterOfPosition(item.node.getStart(sourceFile));
          const evidence = `${relative(sourceFile.fileName)}:${pos.line + 1} -> ${relative(resolution.resolvedFileName)}`;
          const key = `${sourcePackage.file}|${targetPackage.file}`;
          const existing = relations.get(key);
          if (existing) {
            if (existing.specifiers.length < 8 && !existing.specifiers.includes(item.specifier)) existing.specifiers.push(item.specifier);
            if (existing.evidence.length < 8 && !existing.evidence.includes(evidence)) existing.evidence.push(evidence);
          } else {
            relations.set(key, {
              sourceProject: relative(sourcePackage.file),
              targetProject: relative(targetPackage.file),
              specifiers: [item.specifier],
              evidence: [evidence],
            });
          }
        }
      }
    } catch (error) {
      diagnostics.push(`${relative(configPath)}: ${error && error.message ? error.message : String(error)}`);
    }
  }

  process.stdout.write(JSON.stringify({
    available: compilerAvailable,
    analyzedProjects,
    relations: [...relations.values()].map(r => ({
      sourceProject: r.sourceProject,
      targetProject: r.targetProject,
      label: `TypeScript import · ${r.specifiers.slice(0, 3).join(', ')}`,
      evidence: r.evidence.join('; '),
    })),
    diagnostics: diagnostics.slice(0, 20),
  }));
}

try { main(); }
catch (error) {
  process.stdout.write(JSON.stringify({ available: false, analyzedProjects: 0, relations: [], diagnostics: [String(error)] }));
}
