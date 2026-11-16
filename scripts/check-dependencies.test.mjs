import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { staleDependencies } from './check-dependencies.mjs';

function checkout(lockWorkspaces, lockPackages, installed) {
  const root = mkdtempSync(path.join(tmpdir(), 'tinadec-deps-'));
  writeFileSync(path.join(root, 'bun.lock'), JSON.stringify({ lockfileVersion: 1, workspaces: lockWorkspaces, packages: lockPackages }));
  for (const [location, version] of Object.entries(installed)) {
    mkdirSync(path.join(root, location), { recursive: true });
    writeFileSync(path.join(root, location, 'package.json'), JSON.stringify({ version }));
  }
  return root;
}

test('names a dependency the lockfile has but node_modules lacks, and one left at another version', () => {
  const root = checkout({
    '': { devDependencies: { concurrently: '^9' } },
    'apps/desktop': { dependencies: { 'monaco-editor': '^0.55.1', vue: '^3' } },
  }, {
    'node_modules/concurrently': ['concurrently@9.1.0', '', {}, 'sha'],
    'node_modules/monaco-editor': ['monaco-editor@0.55.1', '', {}, 'sha'],
    'node_modules/vue': ['vue@3.6.0', '', {}, 'sha'],
    'node_modules/@esbuild/win32-x64': ['@esbuild/win32-x64@0.25.0', '', { optional: true }, 'sha'],
  }, { 'node_modules/concurrently': '9.1.0', 'node_modules/vue': '3.5.0' });
  try {
    assert.deepEqual(staleDependencies(root), [
      { name: 'monaco-editor', expected: '0.55.1', installed: null },
      { name: 'vue', expected: '3.6.0', installed: '3.5.0' },
    ]);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('finds nothing to report in this checkout, so `bun run dev` is not held back here', () => {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  assert.deepEqual(staleDependencies(root), []);
});
