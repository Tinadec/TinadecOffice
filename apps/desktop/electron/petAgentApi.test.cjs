const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createPetAgentApiBridge } = require('./petAgentApi.cjs');

const GUID = '11111111-2222-3333-4444-555555555555';
const OTHER_GUID = '99999999-8888-7777-6666-555555555555';

function setup({
  ready = true,
  senderOk = true,
  response,
  sessionId = GUID,
  storageId = 'user',
  approvalIds = [GUID],
  approvalSessionId = GUID,
  approvalRunId = null,
  sessionRunIds = [],
  approvalRows,
} = {}) {
  const calls = [];
  const fetchImpl = async (url, init) => {
    calls.push({ url, init });
    const pathname = new URL(url).pathname;
    // Deciding an approval first asks for this window's own pending list (the only approval read
    // the Gateway serves) and refuses unless the id is on it.
    if (pathname === '/api/v1/approvals' && init?.method === 'GET') {
      return {
        ok: true,
        status: 200,
        text: async () => JSON.stringify(approvalRows ?? approvalIds.map((id) => ({
          id,
          tool_id: 'write_file',
          session_id: approvalSessionId,
          run_id: approvalRunId,
        }))),
      };
    }
    if (/^\/api\/v1\/sessions\/[0-9a-f-]{36}\/runs$/i.test(pathname) && init?.method === 'GET') {
      return { ok: true, status: 200, text: async () => JSON.stringify(sessionRunIds.map((id) => ({ id }))) };
    }
    return response ?? { ok: true, status: 200, text: async () => JSON.stringify([{ id: GUID, tool_id: 'write_file' }]) };
  };
  const bridge = createPetAgentApiBridge({
    baseUrl: ready ? 'http://127.0.0.1:48730' : '',
    token: ready ? 'secret-token' : '',
    isSessionPartnerWindowSender: () => senderOk,
    sessionIdForSender: () => sessionId,
    storageIdForSender: () => storageId,
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

test('drops a policy park from another session out of the list it hands back', async () => {
  // Core filters the approval table by session but appends policy parks from the whole storage
  // scope. Handing those to the window would let it show (and decide) another session's park.
  const { bridge, calls } = setup({
    approvalRows: [
      { id: GUID, tool_id: 'write_file', session_id: GUID },
      { id: OTHER_GUID, tool_id: 'shell', run_id: OTHER_GUID },
    ],
    sessionRunIds: [],
  });
  const result = await bridge(event, { method: 'GET', path: `/api/v1/approvals?status=pending&session_id=${GUID}` });
  assert.equal(result.status, 200);
  assert.deepEqual(result.body.map((row) => row.id), [GUID]);
  // One list read plus the run proof the park needs; no extra read when no park is present.
  assert.equal(calls.length, 2);
});

test('keeps a policy park whose run belongs to this window’s session', async () => {
  const { bridge } = setup({
    approvalRows: [{ id: GUID, tool_id: 'shell', run_id: OTHER_GUID }],
    sessionRunIds: [OTHER_GUID],
  });
  const result = await bridge(event, { method: 'GET', path: `/api/v1/approvals?status=pending&session_id=${GUID}` });
  assert.equal(result.status, 200);
  assert.deepEqual(result.body.map((row) => row.id), [GUID]);
});

test('drops an approval row that names neither this session nor one of its runs', async () => {
  const { bridge } = setup({
    approvalRows: [{ id: GUID, tool_id: 'write_file' }],
  });
  const result = await bridge(event, { method: 'GET', path: `/api/v1/approvals?status=pending&session_id=${GUID}` });
  assert.equal(result.status, 200);
  assert.deepEqual(result.body, []);
});

test('allows deciding an approval', async () => {
  const { bridge, calls } = setup({ response: { ok: true, status: 200, text: async () => JSON.stringify({ id: GUID, status: 'approved' }) } });
  const result = await bridge(event, { method: 'POST', path: `/api/v1/approvals/${GUID}/decision`, body: { decision: 'approved', scope: 'once' } });
  assert.equal(result.status, 200);
  const decision = calls.at(-1);
  assert.equal(decision.init.method, 'POST');
  assert.deepEqual(JSON.parse(decision.init.body), { decision: 'approved', scope: 'once' });
});

test('refuses deciding an approval that is not in this window’s pending list', async () => {
  const { bridge, calls } = setup({ approvalIds: [OTHER_GUID] });
  const result = await bridge(event, { method: 'POST', path: `/api/v1/approvals/${GUID}/decision`, body: { decision: 'approved' } });
  assert.equal(result.status, 400);
  // Only the ownership probe went out; the decision itself never did.
  assert.equal(calls.length, 1);
  assert.equal(calls[0].init.method, 'GET');
  assert.equal(calls[0].url, `http://127.0.0.1:48730/api/v1/approvals?status=pending&session_id=${GUID}`);
});

test('refuses a policy park whose run belongs to another session', async () => {
  const { bridge, calls } = setup({
    approvalSessionId: null,
    approvalRunId: GUID,
    sessionRunIds: [OTHER_GUID],
  });
  const result = await bridge(event, { method: 'POST', path: `/api/v1/approvals/${GUID}/decision`, body: { decision: 'approved' } });
  assert.equal(result.status, 400);
  // List probe, then the run proof; the decision itself never went out.
  assert.equal(calls.length, 2);
  assert.equal(calls[1].url, `http://127.0.0.1:48730/api/v1/sessions/${GUID}/runs`);
});

test('allows deciding a policy park whose run belongs to this window’s session', async () => {
  const { bridge, calls } = setup({
    approvalSessionId: null,
    approvalRunId: OTHER_GUID,
    sessionRunIds: [OTHER_GUID],
    response: { ok: true, status: 200, text: async () => JSON.stringify({ id: GUID, status: 'approved' }) },
  });
  const result = await bridge(event, { method: 'POST', path: `/api/v1/approvals/${GUID}/decision`, body: { decision: 'approved' } });
  assert.equal(result.status, 200);
  assert.equal(calls.at(-1).init.method, 'POST');
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

test('allows reading its own session messages and runs', async () => {
  const { bridge, calls } = setup();
  const messages = await bridge(event, { method: 'GET', path: `/api/v1/sessions/${GUID}/messages` });
  const runs = await bridge(event, { method: 'GET', path: `/api/v1/sessions/${GUID}/runs` });
  assert.equal(messages.status, 200);
  assert.equal(runs.status, 200);
  assert.deepEqual(calls.map(call => call.url), [
    `http://127.0.0.1:48730/api/v1/sessions/${GUID}/messages`,
    `http://127.0.0.1:48730/api/v1/sessions/${GUID}/runs`,
  ]);
  assert.equal(calls[0].init.headers['x-tinadec-host-control'], 'secret-token');
});

test('refuses another session even though the path is whitelisted', async () => {
  const { bridge, calls } = setup();
  for (const path of [`/api/v1/sessions/${OTHER_GUID}/messages`, `/api/v1/sessions/${OTHER_GUID}/runs`]) {
    const result = await bridge(event, { method: 'GET', path });
    assert.equal(result.status, 400, path);
    assert.equal(result.body.code, 'invalid_request');
  }
  assert.equal(calls.length, 0);
});

test('refuses session reads without a sender session binding', async () => {
  const { bridge, calls } = setup({ sessionId: null });
  const result = await bridge(event, { method: 'GET', path: `/api/v1/sessions/${GUID}/messages` });
  assert.equal(result.status, 400);
  assert.equal(calls.length, 0);
});

test('refuses writes and unlisted session subpaths', async () => {
  const { bridge, calls } = setup();
  const paths = [
    { method: 'POST', path: `/api/v1/sessions/${GUID}/messages`, body: { content: 'x' } },
    { method: 'GET', path: `/api/v1/sessions/${GUID}/interactions` },
    { method: 'GET', path: `/api/v1/sessions/${GUID}/tool-executions` },
    { method: 'GET', path: `/api/v1/sessions/${GUID}/orchestration` },
  ];
  for (const request of paths) {
    const result = await bridge(event, request);
    assert.equal(result.status, 400, request.path);
  }
  assert.equal(calls.length, 0);
});

test('refuses unlisted query parameters on the session reads', async () => {
  const { bridge, calls } = setup();
  const result = await bridge(event, { method: 'GET', path: `/api/v1/sessions/${GUID}/messages?after_seq=10` });
  assert.equal(result.status, 400);
  assert.equal(calls.length, 0);
});

test('refuses an unfiltered approvals list', async () => {
  const { bridge, calls } = setup();
  const result = await bridge(event, { method: 'GET', path: '/api/v1/approvals?status=pending' });
  assert.equal(result.status, 400);
  assert.equal(calls.length, 0);
});

test('refuses reading another session approvals', async () => {
  const { bridge, calls } = setup();
  const result = await bridge(event, { method: 'GET', path: `/api/v1/approvals?status=pending&session_id=${OTHER_GUID}` });
  assert.equal(result.status, 400);
  assert.equal(calls.length, 0);
});

test('refuses deciding an approval for another session', async () => {
  const { bridge, calls } = setup();
  // The decision route carries no session id; the query form must still be refused so a caller
  // cannot smuggle a session filter through it.
  const result = await bridge(event, { method: 'POST', path: `/api/v1/approvals/${OTHER_GUID}/decision?session_id=${OTHER_GUID}`, body: { decision: 'approved' } });
  assert.equal(result.status, 400);
  assert.equal(calls.length, 0);
});

test('refuses a repeated session_id instead of pinning only the first copy', async () => {
  const { bridge, calls } = setup();
  // The pin reads the first value; the Gateway resolves duplicates to the last one. Forwarding
  // both would pass the check and still be answered for the other session.
  const result = await bridge(event, {
    method: 'GET',
    path: `/api/v1/approvals?status=pending&session_id=${GUID}&session_id=${OTHER_GUID}`,
  });
  assert.equal(result.status, 400);
  assert.equal(calls.length, 0);
});
test('every upstream read carries the storage scope the window was opened in', async () => {
  const { bridge, calls } = setup({ storageId: 'proj-42' });
  await bridge(event, { method: 'GET', path: `/api/v1/sessions/${GUID}/messages` });
  assert.equal(calls.length, 1);
  // Without this header Core resolves the read in the user store, where a project session does
  // not exist, so the window's chat would come back empty with no explanation.
  assert.equal(calls[0].init.headers['x-tinadec-storage-id'], 'proj-42');
});

test('the approval probes carry the storage scope too', async () => {
  const { bridge, calls } = setup({ storageId: 'proj-42' });
  await bridge(event, { method: 'GET', path: `/api/v1/approvals?status=pending&session_id=${GUID}` });
  assert.equal(calls.length, 1);
  assert.equal(calls[0].init.headers['x-tinadec-storage-id'], 'proj-42');
});

test('a missing scope falls back to the user store rather than an empty header', async () => {
  const calls = [];
  const bridge = createPetAgentApiBridge({
    baseUrl: 'http://127.0.0.1:48730',
    token: 'secret-token',
    isSessionPartnerWindowSender: () => true,
    sessionIdForSender: () => GUID,
    storageIdForSender: () => null,
    fetchImpl: async (url, init) => {
      calls.push({ url, init });
      return { ok: true, status: 200, text: async () => '[]' };
    },
  });
  await bridge(event, { method: 'GET', path: `/api/v1/sessions/${GUID}/runs` });
  assert.equal(calls[0].init.headers['x-tinadec-storage-id'], 'user');
});
