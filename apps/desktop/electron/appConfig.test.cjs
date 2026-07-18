const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const {
  DEFAULT_GATEWAY_URL,
  loadAppConfig,
  normalizeGatewayUrl,
  resetGatewayUrl,
  saveGatewayUrl,
} = require('./appConfig.cjs');

test('normalizes and validates Gateway URLs', () => {
  assert.equal(normalizeGatewayUrl(' https://office.example.com/ '), 'https://office.example.com');
  assert.throws(() => normalizeGatewayUrl('file:///tmp/gateway'), /HTTP or HTTPS/);
  assert.throws(() => normalizeGatewayUrl('https://user@example.com'), /credentials/);
});

test('persists a Gateway URL while the environment remains authoritative', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-app-config-'));
  const configFile = path.join(root, 'settings.json');
  try {
    assert.equal(loadAppConfig(configFile, {}).gateway_url, DEFAULT_GATEWAY_URL);
    assert.equal(saveGatewayUrl(configFile, 'https://office.example.com/api/', {}).gateway_url, 'https://office.example.com/api');
    assert.equal(loadAppConfig(configFile, {}).gateway_url, 'https://office.example.com/api');
    assert.equal(loadAppConfig(configFile, { TINADEC_GATEWAY_URL: 'https://managed.example.com' }).source, 'environment');
    assert.throws(() => saveGatewayUrl(configFile, 'https://other.example.com', { TINADEC_GATEWAY_URL: 'https://managed.example.com' }), /managed/);
    assert.equal(resetGatewayUrl(configFile, {}).gateway_url, DEFAULT_GATEWAY_URL);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
