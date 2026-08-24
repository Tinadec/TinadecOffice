import assert from 'node:assert/strict';
import test from 'node:test';
import { coreEndpoint } from './coreClient.js';

test('coreEndpoint resolves API paths against the configured Core URL', () => {
  assert.equal(coreEndpoint('/api/v1/health'), 'http://127.0.0.1:48731/api/v1/health');
});

test('Gateway transport contract forwards current v1 paths unchanged', () => {
  assert.equal(coreEndpoint('/api/v1/user/tool-actions'), 'http://127.0.0.1:48731/api/v1/user/tool-actions');
  assert.equal(coreEndpoint('/api/v1/tool-runtime/tools/git_status/execute'), 'http://127.0.0.1:48731/api/v1/tool-runtime/tools/git_status/execute');
});
