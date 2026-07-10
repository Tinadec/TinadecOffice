import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { executeCodeTool } from './codeTools.js';

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
  } finally {
    await rm(cwd, { recursive: true, force: true });
  }
});

test('list_directory rejects path escape via ..', async () => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-ls-'));
  try {
    const result = await executeCodeTool('list_directory', { cwd, arguments: { path: '../../../etc' } });
    assert.ok(result);
    assert.equal(result.status, 'failed');
    assert.ok(result.evidence.includes('list_directory:rejected-escape'));
  } finally {
    await rm(cwd, { recursive: true, force: true });
  }
});

test('list_directory rejects metacharacters in path', async () => {
  const cwd = await mkdtemp(path.join(tmpdir(), 'tinadec-ls-'));
  try {
    const result = await executeCodeTool('list_directory', { cwd, arguments: { path: 'foo; rm -rf /' } });
    assert.ok(result);
    assert.equal(result.status, 'failed');
    assert.ok(result.evidence.includes('list_directory:rejected-metachar'));
  } finally {
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
    const hiddenNames = ((hidden?.data.entries) as Array<{name:string}>).map((e) => e.name);
    assert.ok(hiddenNames.includes('.hidden'));

    const noHidden = await executeCodeTool('list_directory', { cwd, arguments: { show_hidden: false } });
    const noHiddenNames = ((noHidden?.data.entries) as Array<{name:string}>).map((e) => e.name);
    assert.ok(!noHiddenNames.includes('.hidden'));
  } finally {
    await rm(cwd, { recursive: true, force: true });
  }
});
