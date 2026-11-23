const assert = require('node:assert/strict');
const { mkdtempSync, mkdirSync, rmSync, writeFileSync } = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const { readStartupFailure, documentFile } = require('./startupFailure.cjs');

function withRoot(fn) {
  const root = mkdtempSync(path.join(os.tmpdir(), 'tinadec-startup-failure-'));
  try {
    return fn(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test('a bootstrap receipt names the document the user has to repair', () => withRoot((root) => {
  mkdirSync(path.join(root, 'state'), { recursive: true });
  writeFileSync(path.join(root, 'state', 'startup-failure.json'), JSON.stringify({
    code: 'configuration_missing',
    document: 'storage',
    message: "Configuration document 'storage' is missing; restore it to continue.",
    at: '2026-11-12T06:00:00.0000000+00:00',
  }));
  const failure = readStartupFailure(root);
  assert.equal(failure.code, 'configuration_missing');
  assert.equal(failure.document, 'storage.toml');
  assert.match(failure.message, /is missing/);
}));

test('no receipt, a truncated receipt or a receipt without a code is not a startup failure', () => withRoot((root) => {
  assert.equal(readStartupFailure(root), null);
  mkdirSync(path.join(root, 'state'), { recursive: true });
  writeFileSync(path.join(root, 'state', 'startup-failure.json'), '{"code":"configur');
  assert.equal(readStartupFailure(root), null);
  writeFileSync(path.join(root, 'state', 'startup-failure.json'), JSON.stringify({ document: 'storage' }));
  assert.equal(readStartupFailure(root), null);
}));

test('receipt fields are bounded and document ids become file names', () => withRoot((root) => {
  mkdirSync(path.join(root, 'state'), { recursive: true });
  writeFileSync(path.join(root, 'state', 'startup-failure.json'), JSON.stringify({
    code: 'x'.repeat(200),
    document: 'logging',
    message: 'y'.repeat(2000),
  }));
  const failure = readStartupFailure(root);
  assert.equal(failure.code.length, 60);
  assert.equal(failure.message.length, 400);
  assert.equal(failure.document, 'logging.toml');
  assert.equal(documentFile('storage.toml'), 'storage.toml');
  assert.equal(documentFile(''), null);
  assert.equal(documentFile(42), null);
}));
