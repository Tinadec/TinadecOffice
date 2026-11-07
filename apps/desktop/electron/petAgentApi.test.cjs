const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createPetAgentApiBridge } = require('./petAgentApi.cjs');

const GUID = '11111111-2222-3333-4444-555555555555';

function setup({ ready = true, senderOk = true, response } = {}) {
  const calls = [];
  const fetchImpl = async (url, init) => {
    calls.push({ url, init });
    return response ?? { status: 200, text: async () => JSON.stringify([{ id: GUID, tool_id: 'write_file' }]) };
  };
  const bridge = createPetAgentApiBridge({
    baseUrl: ready ? 'http://127.0.0.1:48730' : '',
    token: ready ? 'secret-token' : '',
    isSessionPartnerWindowSender: () => senderOk,
    fetchImpl,
  });
  return { bridge, calls };
}

const event = { sender: { id: 7 } };

test('rejects a sender that is not a session partner window', async () => {
  const { bridge } = setup({ senderOk: false });
  await assert.rejects(() => bridge(event, { method: 'GET', path: '/api/v1/approvals' }), /session partner window/);
});

test('reports host_not_ready without a credential', async () => {
  const { bridge, calls } = setup({ ready: false });
  const result = await bridge(event, { method: 'GET', path: '/api/v1/approvals' });
  assert.equal(result.status, 503);
  assert.equal(result.body.code, 'host_not_ready');
  assert.equal(calls.length, 0);
});

test('allows reading pending approvals and injects the credential in the main process', async () => {
  const { bridge, calls } = setup();
  const result = await bridge(event, { method: 'GET', path: `/api/v1/approvals?status=pending&session_id=${GUID}` });
  assert.equal(result.status, 200);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, `http://127.0.0.1:48730/api/v1/approvals?status=pending&session_id=${GUID}`);
  assert.equal(calls[0].init.headers['x-tinadec-host-control'], 'secret-token');
  assert.equal(calls[0].init.redirect, 'error');
});

test('allows deciding an approval', async () => {
  const { bridge, calls } = setup({ response: { status: 200, text: async () => JSON.stringify({ id: GUID, status: 'approved' }) } });
  const result = await bridge(event, { method: 'POST', path: `/api/v1/approvals/${GUID}/decision`, body: { decision: 'approved', scope: 'once' } });
  assert.equal(result.status, 200);
  assert.equal(calls[0].init.method, 'POST');
  assert.deepEqual(JSON.parse(calls[0].init.body), { decision: 'approved', scope: 'once' });
});

test('refuses paths outside the approval whitelist', async () => {
  const { bridge, calls } = setup();
  for (const path of ['/api/v1/sessions', '/api/v1/approvals/pre-authorizations', `/api/v1/approvals/${GUID}/gates`, '/api/v1/companions']) {
    const result = await bridge(event, { method: 'GET', path });
    assert.equal(result.status, 400, path);
    assert.equal(result.body.code, 'invalid_request');
  }
  assert.equal(calls.length, 0);
});

test('refuses disallowed query keys and absolute URLs', async () => {
  const { bridge } = setup();
  const badQuery = await bridge(event, { method: 'GET', path: '/api/v1/approvals?tool_id=shell' });
  assert.equal(badQuery.status, 400);
  const absolute = await bridge(event, { method: 'GET', path: 'http://evil/api/v1/approvals' });
  assert.equal(absolute.status, 400);
});

test('refuses a GET that carries a body', async () => {
  const { bridge } = setup();
  const result = await bridge(event, { method: 'GET', path: '/api/v1/approvals', body: { x: 1 } });
  assert.equal(result.status, 400);
});