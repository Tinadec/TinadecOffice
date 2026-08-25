import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = originalFetch;
});

test('full-duplex runtime routes preserve Core paths, query names, and command bodies', { concurrency: false }, async () => {
  const requests: Array<{ url: string; method: string; body: string | undefined }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({
      url: typeof input === 'string' ? input : input.toString(),
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : undefined
    });
    return new Response(JSON.stringify({ proxied: true }), {
      status: 202,
      headers: { 'content-type': 'application/json' }
    });
  }) as typeof fetch;

  const calls = [
    new Request('http://gateway.local/api/v1/application-modes'),
    new Request('http://gateway.local/api/v1/agent-modes?application_mode=space'),
    new Request('http://gateway.local/api/v1/runs/run-1/orchestration'),
    new Request('http://gateway.local/api/v1/runs/run-1/agent-lineage'),
    new Request('http://gateway.local/api/v1/sessions/session-1/context-versions?run_id=run-1&limit=12'),
    new Request('http://gateway.local/api/v1/memory-candidates?status=proposed&run_id=run-1'),
    new Request('http://gateway.local/api/v1/agent-candidates?status=proposed&run_id=run-1'),
    new Request('http://gateway.local/api/v1/runs/run-1/control', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ command: 'pause', expected_context_revision: 7 })
    }),
    new Request('http://gateway.local/api/v1/memory-candidates/memory-1/promote', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ reason: 'confirmed by reviewer' })
    }),
    new Request('http://gateway.local/api/v1/agent-candidates/agent-1/reject', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ reason: 'insufficient evidence' })
    })
  ];

  const responses: Response[] = [];
  for (const request of calls) responses.push(await app.handle(request));
  for (const response of responses) {
    assert.equal(response.status, 202);
    assert.deepEqual(await response.json(), { proxied: true });
  }

  assert.deepEqual(requests.map((request) => [request.method, request.url]), [
    ['GET', 'http://127.0.0.1:48731/api/v1/application-modes'],
    ['GET', 'http://127.0.0.1:48731/api/v1/agent-modes?application_mode=space'],
    ['GET', 'http://127.0.0.1:48731/api/v1/runs/run-1/orchestration'],
    ['GET', 'http://127.0.0.1:48731/api/v1/runs/run-1/agent-lineage'],
    ['GET', 'http://127.0.0.1:48731/api/v1/sessions/session-1/context-versions?run_id=run-1&limit=12'],
    ['GET', 'http://127.0.0.1:48731/api/v1/memory-candidates?status=proposed&run_id=run-1'],
    ['GET', 'http://127.0.0.1:48731/api/v1/agent-candidates?status=proposed&run_id=run-1'],
    ['POST', 'http://127.0.0.1:48731/api/v1/runs/run-1/control'],
    ['POST', 'http://127.0.0.1:48731/api/v1/memory-candidates/memory-1/promote'],
    ['POST', 'http://127.0.0.1:48731/api/v1/agent-candidates/agent-1/reject']
  ]);
  assert.deepEqual(JSON.parse(requests[7]!.body ?? ''), { command: 'pause', expected_context_revision: 7 });
  assert.deepEqual(JSON.parse(requests[8]!.body ?? ''), { reason: 'confirmed by reviewer' });
  assert.deepEqual(JSON.parse(requests[9]!.body ?? ''), { reason: 'insufficient evidence' });
});

test('workspace governance routes stay stateless Core proxies and preserve If-Match/ETag', { concurrency: false }, async () => {
  const requests: Array<{ url: string; method: string; body: string | undefined; headers: Headers }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({
      url: String(input),
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : undefined,
      headers: new Headers(init?.headers),
    });
    return new Response(JSON.stringify({ proxied: true }), {
      status: 200,
      headers: { 'content-type': 'application/json', etag: '"7"' },
    });
  }) as typeof fetch;

  const headers = { 'content-type': 'application/json', 'if-match': '"6"' };
  const calls = [
    new Request('http://gateway.local/api/v1/projects/project-1/snapshots', { method: 'POST', headers, body: JSON.stringify({ idempotency_key: 'snap-1' }) }),
    new Request('http://gateway.local/api/v1/projects/project-1/snapshots'),
    new Request('http://gateway.local/api/v1/workspace-snapshots/snapshot-1'),
    new Request('http://gateway.local/api/v1/workspace-snapshots/snapshot-1/restore', { method: 'POST', headers, body: JSON.stringify({ allow_conflicts: true }) }),
    new Request('http://gateway.local/api/v1/workspace-defaults'),
    new Request('http://gateway.local/api/v1/workspace-defaults/draft', { method: 'PUT', headers, body: JSON.stringify({ default_agent_mode_id: 'mode-1' }) }),
    new Request('http://gateway.local/api/v1/workspace-defaults/publish', { method: 'POST', headers }),
    new Request('http://gateway.local/api/v1/workspace-defaults/archive', { method: 'POST', headers }),
  ];

  const responses: Response[] = [];
  for (const call of calls) responses.push(await app.handle(call));
  for (const response of responses) {
    assert.equal(response.status, 200);
    assert.equal(response.headers.get('etag'), '"7"');
    assert.deepEqual(await response.json(), { proxied: true });
  }

  assert.deepEqual(requests.map((request) => [request.method, request.url]), [
    ['POST', 'http://127.0.0.1:48731/api/v1/projects/project-1/snapshots'],
    ['GET', 'http://127.0.0.1:48731/api/v1/projects/project-1/snapshots'],
    ['GET', 'http://127.0.0.1:48731/api/v1/workspace-snapshots/snapshot-1'],
    ['POST', 'http://127.0.0.1:48731/api/v1/workspace-snapshots/snapshot-1/restore'],
    ['GET', 'http://127.0.0.1:48731/api/v1/workspace-defaults'],
    ['PUT', 'http://127.0.0.1:48731/api/v1/workspace-defaults/draft'],
    ['POST', 'http://127.0.0.1:48731/api/v1/workspace-defaults/publish'],
    ['POST', 'http://127.0.0.1:48731/api/v1/workspace-defaults/archive'],
  ]);
  assert.equal(requests[0]!.headers.get('if-match'), '"6"');
  assert.deepEqual(JSON.parse(requests[5]!.body ?? ''), { default_agent_mode_id: 'mode-1' });
});

test('agent pack routes are stateless Core proxies and preserve install guards', { concurrency: false }, async () => {
  const requests: Array<{ url: string; method: string; body: string | undefined; headers: Headers }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({
      url: String(input),
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : undefined,
      headers: new Headers(init?.headers),
    });
    return new Response(JSON.stringify({ status: 'up_to_date' }), {
      status: 200,
      headers: { 'content-type': 'application/json', etag: '"pack-revision-2"' },
    });
  }) as typeof fetch;

  const envelope = { api_version: 'tinadec.io/agent-pack/v1alpha1', pack_id: 'tinadec.office.agent-pack' };
  const encodedPackId = encodeURIComponent(envelope.pack_id);
  const calls = [
    new Request('http://gateway.local/api/v1/agent-packs'),
    new Request(`http://gateway.local/api/v1/agent-packs/${encodedPackId}`),
    new Request('http://gateway.local/api/v1/agent-packs/install-preview', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(envelope),
    }),
    new Request(`http://gateway.local/api/v1/agent-packs/${encodedPackId}`, {
      method: 'PUT',
      headers: {
        'content-type': 'application/json',
        'if-match': '"pack-revision-1"',
        'idempotency-key': 'office-pack-0.1.0',
      },
      body: JSON.stringify({ envelope, preview_id: 'preview-1' }),
    }),
  ];

  for (const call of calls) {
    const response = await app.handle(call);
    assert.equal(response.status, 200);
    assert.equal(response.headers.get('etag'), '"pack-revision-2"');
    assert.deepEqual(await response.json(), { status: 'up_to_date' });
  }

  assert.deepEqual(requests.map(({ method, url }) => [method, url]), [
    ['GET', 'http://127.0.0.1:48731/api/v1/agent-packs'],
    ['GET', `http://127.0.0.1:48731/api/v1/agent-packs/${encodedPackId}`],
    ['POST', 'http://127.0.0.1:48731/api/v1/agent-packs/install-preview'],
    ['PUT', `http://127.0.0.1:48731/api/v1/agent-packs/${encodedPackId}`],
  ]);
  assert.deepEqual(JSON.parse(requests[2]!.body ?? ''), envelope);
  assert.deepEqual(JSON.parse(requests[3]!.body ?? ''), { envelope, preview_id: 'preview-1' });
  assert.equal(requests[3]!.headers.get('if-match'), '"pack-revision-1"');
  assert.equal(requests[3]!.headers.get('idempotency-key'), 'office-pack-0.1.0');
});

test('agent pack routes preserve public RFC9457 error codes', { concurrency: false }, async () => {
  const publicCodes = [
    'invalid_agent_pack_manifest',
    'agent_pack_management_forbidden',
    'agent_pack_version_hash_conflict',
    'agent_pack_resource_conflict',
    'agent_pack_revision_conflict',
    'agent_pack_incompatible',
    'agent_pack_not_found',
    'agent_pack_owner_conflict',
    'agent_pack_preview_stale',
    'managed_resource_read_only',
  ];
  let coreCode = publicCodes[0]!;
  globalThis.fetch = (async () => new Response(JSON.stringify({
    type: `https://tinadec.dev/errors/${coreCode}`,
    title: coreCode,
    status: 409,
    detail: 'Agent pack request failed.',
    code: coreCode,
    trace_id: 'trace-pack-conflict',
  }), {
    status: 409,
    headers: { 'content-type': 'application/problem+json' },
  })) as unknown as typeof fetch;

  for (const code of publicCodes) {
    coreCode = code;
    const response = await app.handle(new Request('http://gateway.local/api/v1/agent-packs/install-preview', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ api_version: 'tinadec.io/agent-pack/v1alpha1' }),
    }));

    assert.equal(response.status, 409);
    assert.equal(response.headers.get('content-type'), 'application/problem+json');
    assert.deepEqual(await response.json(), {
      type: `https://tinadec.dev/errors/${code}`,
      title: code,
      status: 409,
      detail: 'Agent pack request failed.',
      code,
      instance: '/api/v1/agent-packs/install-preview',
      trace_id: 'trace-pack-conflict',
    });
  }
});

test('governance control routes proxy decisions and grants without local authorization', { concurrency: false }, async () => {
  const requests: Array<{ url: string; method: string; body: string | undefined }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), method: init?.method ?? 'GET', body: typeof init?.body === 'string' ? init.body : undefined });
    return new Response(JSON.stringify({ proxied: true }), { status: 202, headers: { 'content-type': 'application/json' } });
  }) as typeof fetch;

  const calls = [
    new Request('http://gateway.local/api/v1/governance/permission-requests?status=pending&run_id=run-1'),
    new Request('http://gateway.local/api/v1/governance/permission-requests/request-1'),
    new Request('http://gateway.local/api/v1/governance/permission-requests/request-1/decision', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ approve: true }) }),
    new Request('http://gateway.local/api/v1/governance/policy-bundles', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ slug: 'workspace' }) }),
    new Request('http://gateway.local/api/v1/governance/policy-bundles/bundle-1/versions', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ rules: [] }) }),
    new Request('http://gateway.local/api/v1/governance/policy-bundles/bundle-1/archive', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ reason: 'retired' }) }),
    new Request('http://gateway.local/api/v1/governance/capability-grants', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ capability: 'tool.invoke' }) }),
    new Request('http://gateway.local/api/v1/governance/capability-grants/grant-1/revoke', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ reason: 'revoked' }) }),
    new Request('http://gateway.local/api/v1/governance/approval-delegations', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ max_risk: 'low' }) }),
    new Request('http://gateway.local/api/v1/governance/approval-delegations/delegation-1/revoke', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ reason: 'revoked' }) }),
    new Request('http://gateway.local/api/v1/governance/capability-leases/lease-1/revoke', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ reason: 'revoked' }) }),
  ];
  for (const call of calls) {
    const response = await app.handle(call);
    assert.equal(response.status, 202);
    assert.deepEqual(await response.json(), { proxied: true });
  }
  assert.deepEqual(requests.map((request) => [request.method, request.url]), [
    ['GET', 'http://127.0.0.1:48731/api/v1/governance/permission-requests?status=pending&run_id=run-1'],
    ['GET', 'http://127.0.0.1:48731/api/v1/governance/permission-requests/request-1'],
    ['POST', 'http://127.0.0.1:48731/api/v1/governance/permission-requests/request-1/decision'],
    ['POST', 'http://127.0.0.1:48731/api/v1/governance/policy-bundles'],
    ['POST', 'http://127.0.0.1:48731/api/v1/governance/policy-bundles/bundle-1/versions'],
    ['POST', 'http://127.0.0.1:48731/api/v1/governance/policy-bundles/bundle-1/archive'],
    ['POST', 'http://127.0.0.1:48731/api/v1/governance/capability-grants'],
    ['POST', 'http://127.0.0.1:48731/api/v1/governance/capability-grants/grant-1/revoke'],
    ['POST', 'http://127.0.0.1:48731/api/v1/governance/approval-delegations'],
    ['POST', 'http://127.0.0.1:48731/api/v1/governance/approval-delegations/delegation-1/revoke'],
    ['POST', 'http://127.0.0.1:48731/api/v1/governance/capability-leases/lease-1/revoke'],
  ]);
  assert.deepEqual(JSON.parse(requests[2]!.body ?? ''), { approve: true });
});

test('invoke-stream preserves the full-duplex request envelope and Core SSE response', { concurrency: false }, async () => {
  let forwarded: { url: string; method: string; body: string | undefined } | undefined;
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    forwarded = {
      url: typeof input === 'string' ? input : input.toString(),
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : undefined
    };
    return new Response('data: {"kind":"ack","seq":1}\n\n', {
      status: 200,
      headers: { 'content-type': 'text/event-stream' }
    });
  }) as typeof fetch;

  const envelope = {
    content: 'Implement the approved change.',
    client_message_id: 'client-message-1',
    application_mode: 'space',
    agent_mode: 'agent',
    permission_mode: 'ask',
    target_run_id: 'run-1',
    expected_context_revision: 7
  };
  const response = await app.handle(new Request('http://gateway.local/api/v1/sessions/session-1/invoke-stream', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(envelope)
  }));

  assert.equal(response.status, 200);
  assert.match(response.headers.get('content-type') ?? '', /^text\/event-stream/);
  assert.equal(await response.text(), 'data: {"kind":"ack","seq":1}\n\n');
  assert.deepEqual(forwarded, {
    url: 'http://127.0.0.1:48731/api/v1/sessions/session-1/invoke-stream',
    method: 'POST',
    body: JSON.stringify(envelope)
  });
});

test('tool catalog routes are Core-owned and do not use Gateway risk metadata', { concurrency: false }, async () => {
  let forwarded: { url: string; method: string } | undefined;
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    forwarded = { url: String(input), method: init?.method ?? 'GET' };
    return new Response(JSON.stringify([{ id: 'provider.tool', risk: 'provider-owned' }]), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    });
  }) as typeof fetch;

  const response = await app.handle(new Request('http://gateway.local/api/v1/code/tools?domain=code'));

  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), [{ id: 'provider.tool', risk: 'provider-owned' }]);
  assert.deepEqual(forwarded, {
    url: 'http://127.0.0.1:48731/api/v1/tools?domain=code',
    method: 'GET',
  });
});

test('user tool action routes are stateless Core proxies', { concurrency: false }, async () => {
  const requests: Array<{ url: string; method: string; body: string | undefined }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({
      url: String(input),
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : undefined,
    });
    return new Response(JSON.stringify({ status: 'awaiting_user' }), {
      status: 202,
      headers: { 'content-type': 'application/json', etag: '"action-1"' },
    });
  }) as typeof fetch;

  const calls = [
    new Request('http://gateway.local/api/v1/user/tool-actions?status=awaiting_user'),
    new Request('http://gateway.local/api/v1/user/tool-actions', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ tool_id: 'write_file', parameters: { path: 'a.txt' } }),
    }),
    new Request('http://gateway.local/api/v1/user/tool-actions/action-1'),
    new Request('http://gateway.local/api/v1/user/tool-actions/action-1/resume', {
      method: 'POST',
    }),
    new Request('http://gateway.local/api/v1/user/tool-actions/action-1/snapshot-override', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ reason: 'user accepted non-reversible change' }),
    }),
    new Request('http://gateway.local/api/v1/user/tool-actions/action-1/recovery-decision', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ decision: 'mark_failed', reason: 'verified no remote change' }),
    }),
  ];

  for (const call of calls) {
    const response = await app.handle(call);
    assert.equal(response.status, 202);
    assert.deepEqual(await response.json(), { status: 'awaiting_user' });
  }

  assert.deepEqual(requests.map(({ method, url }) => [method, url]), [
    ['GET', 'http://127.0.0.1:48731/api/v1/user/tool-actions?status=awaiting_user'],
    ['POST', 'http://127.0.0.1:48731/api/v1/user/tool-actions'],
    ['GET', 'http://127.0.0.1:48731/api/v1/user/tool-actions/action-1'],
    ['POST', 'http://127.0.0.1:48731/api/v1/user/tool-actions/action-1/resume'],
    ['POST', 'http://127.0.0.1:48731/api/v1/user/tool-actions/action-1/snapshot-override'],
    ['POST', 'http://127.0.0.1:48731/api/v1/user/tool-actions/action-1/recovery-decision'],
  ]);
  assert.deepEqual(JSON.parse(requests[1]!.body ?? ''), { tool_id: 'write_file', parameters: { path: 'a.txt' } });
  assert.equal(requests[3]!.body, undefined);
  assert.deepEqual(JSON.parse(requests[4]!.body ?? ''), { reason: 'user accepted non-reversible change' });
  assert.deepEqual(JSON.parse(requests[5]!.body ?? ''), { decision: 'mark_failed', reason: 'verified no remote change' });
});

test('tool provider execution is a transport-only facade', { concurrency: false }, async () => {
  let forwarded: { url: string; method: string; body: string | undefined; headers: Headers } | undefined;
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    forwarded = { url: String(input), method: init?.method ?? 'GET', body: typeof init?.body === 'string' ? init.body : undefined, headers: new Headers(init?.headers) };
    return new Response(JSON.stringify({ ok: true }), { status: 202, headers: { 'content-type': 'application/json' } });
  }) as typeof fetch;

  const body = { session_id: 'session-1', approval_id: 'approval-1', command: 'git status', approved: false };
  const response = await app.handle(new Request('http://gateway.local/api/v1/tool-runtime/tools/command_run/execute', {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-request-id': 'request-1', 'x-user-id': 'user-1' },
    body: JSON.stringify(body),
  }));

  assert.equal(response.status, 202);
  assert.deepEqual(await response.json(), { ok: true });
  assert.equal(forwarded?.url, 'http://127.0.0.1:48732/api/v1/tools/command_run/execute');
  assert.equal(forwarded?.method, 'POST');
  assert.deepEqual(JSON.parse(forwarded?.body ?? ''), body);
  assert.equal(forwarded?.headers.get('x-request-id'), 'request-1');
  assert.equal(forwarded?.headers.get('x-user-id'), 'user-1');
});

test('code tools remain the current v1 direct user transport and preserve provider errors', { concurrency: false }, async () => {
  let forwarded: { url: string; method: string; body: string | undefined; headers: Headers } | undefined;
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    forwarded = {
      url: String(input),
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : undefined,
      headers: new Headers(init?.headers),
    };
    return new Response(JSON.stringify({ code: 'provider_denied', detail: 'Tool provider rejected the user request.' }), {
      status: 422,
      headers: { 'content-type': 'application/problem+json', etag: '"provider-4"' },
    });
  }) as typeof fetch;

  const body = {
    cwd: 'C:/workspace',
    source: 'human',
    approval: true,
    arguments: { path: 'README.md' },
  };
  const response = await app.handle(new Request('http://gateway.local/api/v1/code/tools/read_file/execute', {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-request-id': 'request-direct-1', 'x-tenant-id': 'tenant-1' },
    body: JSON.stringify(body),
  }));

  assert.equal(response.status, 422);
  assert.equal(response.headers.get('content-type'), 'application/problem+json');
  assert.equal(response.headers.get('etag'), '"provider-4"');
  assert.deepEqual(await response.json(), { code: 'provider_denied', detail: 'Tool provider rejected the user request.' });
  assert.equal(forwarded?.url, 'http://127.0.0.1:48732/api/v1/tools/read_file/execute');
  assert.equal(forwarded?.method, 'POST');
  assert.deepEqual(JSON.parse(forwarded?.body ?? ''), body);
  assert.equal(forwarded?.headers.get('x-request-id'), 'request-direct-1');
  assert.equal(forwarded?.headers.get('x-tenant-id'), 'tenant-1');
});
