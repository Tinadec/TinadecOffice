import test from 'node:test';
import assert from 'node:assert/strict';
import { developmentBackendIsReady } from './backendReadiness.mjs';
const setup = (overrides = {}) => ({ gatewayUrl: 'http://127.0.0.1:48730', coreUrl: 'http://127.0.0.1:48731', token: 'a'.repeat(43),
  probeJson: async url => url.includes('48730') ? { status: 200, data: { core_status: 'ready' } } : { status: 200, data: { name: 'tinadec-core' } },
  verifyIdentity: async () => true, ...overrides });
test('ready Core alone cannot claim shared backend readiness while Gateway is unavailable', async () => {
  let proofs = 0;
  const result = await developmentBackendIsReady(setup({ probeJson: async url => url.includes('48730')
    ? { status: 503, data: { core_status: 'unreachable' } } : { status: 200, data: { name: 'tinadec-core' } },
    verifyIdentity: async () => { proofs++; } }));
  assert.equal(result, false); assert.equal(proofs, 0);
});
test('both ready services must prove the same launch key and their respective roles', async () => {
  const proofs = [];
  assert.equal(await developmentBackendIsReady(setup({ verifyIdentity: async (...args) => { proofs.push(args); return true; } })), true);
  assert.deepEqual(proofs.map(x => x.slice(1)), [['core', 'a'.repeat(43)], ['gateway', 'a'.repeat(43)]]);
});
test('temporary challenge failures wait while wrong identity reports an actionable safe failure', async () => {
  assert.equal(await developmentBackendIsReady(setup({ verifyIdentity: async () => { throw new Error('fetch failed'); } })), false);
  await assert.rejects(developmentBackendIsReady(setup({ verifyIdentity: async () => {
    throw Object.assign(new Error('private-key-value'), { code: 'host_identity_rejected' });
  } })), error => error.message.includes('npm run dev') && !error.message.includes('private-key-value'));
});
