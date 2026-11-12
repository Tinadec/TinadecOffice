const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const source = fs.readFileSync(path.join(__dirname, 'denvWindow.cjs'), 'utf8');

test('Denv is an independent framed app window with its own route', () => {
  assert.match(source, /new BrowserWindow\(\{/);
  assert.match(source, /minWidth:\s*980/);
  assert.match(source, /titleBarStyle:\s*'hidden'/);
  assert.match(source, /#\/denv/);
  assert.match(source, /hash:\s*'\/denv'/);
  assert.match(source, /registerTrustedHostWindow\(win\)/);
});

test('opening Denv reuses its existing window instead of stacking duplicates', () => {
  assert.match(source, /if \(denvWindow && !denvWindow\.isDestroyed\(\)\)/);
  assert.match(source, /denvWindow\.focus\(\)/);
});
