const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { createWorkbenchLayoutStore } = require('./workbenchLayoutStore.cjs');

test('writes workbench layout documents atomically and repairs malformed reads with null', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-workbench-'));
  const filePath = path.join(root, 'state', 'workbench-layout.json');
  try {
    const store = createWorkbenchLayoutStore(filePath);
    assert.equal(store.load(), null);
    const document = { version: 1, revision: 3, pages: {}, workspacePages: {}, globalDefault: null };
    assert.deepEqual(store.save(document), document);
    assert.deepEqual(store.load(), document);
    fs.writeFileSync(filePath, '{not-json', 'utf8');
    assert.equal(store.load(), null);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('rejects invalid or oversized layout writes', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-workbench-'));
  try {
    const store = createWorkbenchLayoutStore(path.join(root, 'layout.json'));
    assert.throws(() => store.save({ version: 2 }), /Invalid/);
    assert.throws(() => store.save({ version: 1, payload: 'x'.repeat(5 * 1024 * 1024) }), /too large/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
