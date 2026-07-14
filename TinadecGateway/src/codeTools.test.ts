import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test, { after } from 'node:test';
import { executeCodeTool } from './codeTools.js';
import { disposeToolLayerProcesses, disposeToolLayerWorkspace } from './toolLayerBridge.js';

after(() => disposeToolLayerProcesses());

test('list_directory lists entries with directories first', async () => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-ls-'));
  try {
    await writeFile(path.join(cwd, 'file-a.txt'), 'a');
    await writeFile(path.join(cwd, 'file-b.txt'), 'b');
    await mkdir(path.join(cwd, 'subdir'));

    const result = await executeCodeTool('list_directory', { cwd, arguments: {} });
    assert.ok(result, 'result should not be null');
    assert.equal(result.status, 'completed');
    const entries = result.data.entries as Array<{ name: string; is_directory: boolean }>;
    assert.ok(entries);
    assert.equal(entries.length, 3);
    assert.equal(entries[0].name, 'subdir');
    assert.equal(entries[0].is_directory, true);
    const names = entries.map((e) => e.name).sort();
    assert.deepEqual(names, ['file-a.txt', 'file-b.txt', 'subdir']);
    assert.ok(result.evidence.includes('list_directory:tool-layer'));
  } finally {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  }
});

test('list_directory rejects path escape via ..', async () => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-ls-'));
  try {
    const result = await executeCodeTool('list_directory', { cwd, arguments: { path: '../../../etc' } });
    assert.ok(result);
    assert.equal(result.status, 'failed');
    assert.ok(result.evidence.includes('list_directory:tool-layer-rejected'));
  } finally {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  }
});

test('list_directory treats shell metacharacters as a literal C# tool path', async () => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-ls-'));
  try {
    const result = await executeCodeTool('list_directory', { cwd, arguments: { path: 'foo; rm -rf /' } });
    assert.ok(result);
    assert.equal(result.status, 'failed');
    assert.ok(result.evidence.includes('list_directory:tool-layer-rejected'));
  } finally {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  }
});

test('list_directory rejects missing cwd', async () => {
  const result = await executeCodeTool('list_directory', { arguments: { path: '.' } });
  assert.ok(result);
  assert.equal(result.status, 'failed');
  assert.ok(result.evidence.includes('list_directory:missing-cwd'));
});

test('list_directory respects show_hidden flag', async () => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-ls-'));
  try {
    await writeFile(path.join(cwd, '.hidden'), 'h');
    await writeFile(path.join(cwd, 'visible.txt'), 'v');

    const hidden = await executeCodeTool('list_directory', { cwd, arguments: { show_hidden: true } });
    const hiddenEntries = hidden?.data.entries as Array<{ name: string; is_hidden: boolean }>;
    const hiddenNames = hiddenEntries.map((entry) => entry.name);
    assert.ok(hiddenNames.includes('.hidden'));

    const noHidden = await executeCodeTool('list_directory', { cwd, arguments: { show_hidden: false } });
    const noHiddenNames = ((noHidden?.data.entries) as Array<{ name: string }>).map((entry) => entry.name);
    for (const entry of hiddenEntries.filter((candidate) => candidate.is_hidden)) {
      assert.ok(!noHiddenNames.includes(entry.name));
    }
  } finally {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  }
});

test('search_files and glob_search use the workspace TinadecTools process', async () => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-search-'));
  try {
    await mkdir(path.join(cwd, 'src'));
    await writeFile(path.join(cwd, 'src', 'match.ts'), 'const needle = true;\n');
    await writeFile(path.join(cwd, 'src', 'skip.js'), 'const needle = false;\n');

    const textSearch = await executeCodeTool('search_files', {
      cwd,
      arguments: { query: 'needle' }
    });
    assert.ok(textSearch);
    assert.equal(textSearch.status, 'completed');
    assert.ok(textSearch.evidence.includes('search_files:tool-layer'));
    assert.equal((textSearch.data.lines as unknown[]).length, 2);

    const globSearch = await executeCodeTool('glob_search', {
      cwd,
      arguments: { pattern: '**/*.ts' }
    });
    assert.ok(globSearch);
    assert.equal(globSearch.status, 'completed');
    assert.ok(globSearch.evidence.includes('glob_search:tool-layer'));
    const lines = globSearch.data.lines as Array<{ filepath: string }>;
    assert.equal(lines.length, 1);
    assert.match(lines[0]?.filepath ?? '', /src[\\/]match\.ts$/);
  } finally {
    await disposeToolLayerWorkspace(cwd);
    await rm(cwd, { recursive: true, force: true });
  }
});
