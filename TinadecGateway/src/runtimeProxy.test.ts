import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;

test('session creation and listing preserve the immutable presentation family', { concurrency: false }, async () => {
  let sent: Record<string, unknown> | undefined;
  globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
    if (init?.method === 'POST') {
      sent = JSON.parse(String(init.body));
      return new Response(JSON.stringify({ id: 'space-session', title: 'Space', view_mode: 'space' }), { status: 201, headers: { 'content-type': 'application/json' } });
    }
    return new Response(JSON.stringify([{ id: 'old-session' }, { id: 'space-session', view_mode: 'space' }]), { headers: { 'content-type': 'application/json' } });
  }) as typeof fetch;
  const created = await app.handle(new Request('http://gateway.local/api/v1/sessions', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ title: 'Space', view_mode: 'space' }) }));
  assert.equal(created.status, 201);
  assert.equal(sent?.view_mode, 'space');
  assert.equal((await created.json() as { view_mode: string }).view_mode, 'space');
  const listed = await (await app.handle(new Request('http://gateway.local/api/v1/sessions'))).json() as { view_mode: string }[];
  assert.deepEqual(listed.map(s => s.view_mode), ['flat', 'space']);
});

test('composer settings and admission preserve options, reset flags and revision conflicts', { concurrency: false }, async () => {
  const options = { plan_first: true, spec_enabled: false, multi_agent: false, workflow_mode_version_id: null, bulletin_board: false, worktree: true };
  const requests: Array<{ url: string; body: unknown }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), body: JSON.parse(String(init?.body)) });
    return new Response(JSON.stringify({ id: 'space', view_mode: 'space', space_options: options, permission_mode: 'default', settings_revision: 2 }), { headers: { 'content-type': 'application/json' } });
  }) as typeof fetch;
  const patch = { space_options: options, clear_meeting_model_override: true, clear_mode_version: true, expected_settings_revision: 1 };
  const saved = await app.handle(new Request('http://gateway.local/api/v1/sessions/space', { method: 'PATCH', headers: { 'content-type': 'application/json' }, body: JSON.stringify(patch) }));
  assert.equal(saved.status, 200);
  assert.deepEqual(requests[0]?.body, patch);
  assert.deepEqual((await saved.json() as { space_options: unknown }).space_options, options);
  const submission = { content: 'work', client_message_id: 'client-1', dispatch_mode: 'queued', space_options: options, clear_meeting_model_override: true, expected_settings_revision: 2 };
  const admitted = await app.handle(new Request('http://gateway.local/api/v1/sessions/space/interactions', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(submission) }));
  assert.equal(admitted.status, 200);
  assert.deepEqual(requests[1]?.body, submission);
  globalThis.fetch = (async () => new Response(JSON.stringify({ code: 'session_settings_conflict', detail: 'Settings changed in another window.' }), { status: 409, headers: { 'content-type': 'application/problem+json' } })) as typeof fetch;
  const conflict = await app.handle(new Request('http://gateway.local/api/v1/sessions/space', { method: 'PATCH', headers: { 'content-type': 'application/json' }, body: JSON.stringify(patch) }));
  assert.equal(conflict.status, 409);
  assert.equal((await conflict.json() as { code: string }).code, 'session_settings_conflict');
});

test('run SSE forwards live answer frames before the upstream completes', { concurrency: false, timeout: 5000 }, async () => {
  const first = 'id: 3\nevent: answer.delta\ndata: {"kind":"answer.delta","delta":"Hello"}\n\n';
  let upstream!: ReadableStreamDefaultController<Uint8Array>;
  globalThis.fetch = (async () => new Response(new ReadableStream<Uint8Array>({
    start(controller) { upstream = controller; controller.enqueue(new TextEncoder().encode(first)); },
  }), { headers: { 'content-type': 'text/event-stream' } })) as typeof fetch;
  try {
    const response = await app.handle(new Request('http://gateway.local/api/v1/runs/run-1/stream?after_seq=2'));
    const reader = response.body!.getReader();
    const read = await reader.read();
    assert.equal(read.done, false);
    assert.equal(new TextDecoder().decode(read.value), first);
    upstream.close();
    assert.equal((await reader.read()).done, true);
  } finally {
    try { upstream?.close(); } catch { /* already closed */ }
  }
});

test('run SSE forwards the downstream abort signal to Core', { concurrency: false, timeout: 5000 }, async () => {
  const controller = new AbortController();
  let receivedSignal: AbortSignal | undefined;
  globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
    receivedSignal = init?.signal as AbortSignal | undefined;
    return new Response(new ReadableStream<Uint8Array>({
      start(stream) { stream.enqueue(new TextEncoder().encode(': heartbeat\n\n')); },
      cancel() {},
    }), { headers: { 'content-type': 'text/event-stream' } });
  }) as typeof fetch;
  try {
    const response = await app.handle(new Request('http://gateway.local/api/v1/runs/run-1/stream', { signal: controller.signal }));
    assert.equal(receivedSignal, controller.signal);
    await response.body?.cancel();
  } finally {
    controller.abort();
  }
});

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
    // The retired six-value mode selector is gone from the contract; a stale
    // client query must not reach Core (Core would answer 200 and ignore it,
    // so the Gateway drops it and keeps the wire shape honest).
    new Request('http://gateway.local/api/v1/agent-modes?application_mode=space'),
    new Request('http://gateway.local/api/v1/agent-modes?status=published'),
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
    ['GET', 'http://127.0.0.1:48731/api/v1/agent-modes'],
    ['GET', 'http://127.0.0.1:48731/api/v1/agent-modes?status=published'],
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
    new Request('http://gateway.local/api/v1/workspace-snapshots/snapshot-1/files'),
    new Request('http://gateway.local/api/v1/workspace-snapshots/snapshot-1/files/diff?path=src%2Fnote.txt'),
    new Request('http://gateway.local/api/v1/workspace-snapshots/snapshot-1/files/restore', { method: 'POST', headers, body: JSON.stringify({ path: 'gone.txt', expected_sha256: '' }) }),
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
    ['GET', 'http://127.0.0.1:48731/api/v1/workspace-snapshots/snapshot-1/files'],
    // The path survives as a query value on the URL Core reads it from. A proxy that parsed and
    // dropped it would answer every diff with Core's "path is required" 400, so this entry is the
    // whole proof that the diff route can be used at all.
    ['GET', 'http://127.0.0.1:48731/api/v1/workspace-snapshots/snapshot-1/files/diff?path=src%2Fnote.txt'],
    ['POST', 'http://127.0.0.1:48731/api/v1/workspace-snapshots/snapshot-1/files/restore'],
  ]);
  assert.equal(requests[0]!.headers.get('if-match'), '"6"');
  assert.deepEqual(JSON.parse(requests[5]!.body ?? ''), { default_agent_mode_id: 'mode-1' });
  // `expected_sha256: ""` means "this file was absent when I looked" and must not be rewritten into
  // a missing field on the way through — that distinction is the restore guard for an undelete.
  assert.deepEqual(JSON.parse(requests[10]!.body ?? ''), { path: 'gone.txt', expected_sha256: '' });
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
      category: 'user_action_required', retryable: false, actions: [],
    });
  }
});

test('pack preview and apply preserve configuration diagnostics through actual routes', { concurrency: false }, async () => {
  const diagnostic = { code: 'configuration_unique', message: 'agents.toml: duplicate draft slug meeting.', severity: 'error', line: 12, column: 4 };
  globalThis.fetch = (async () => new Response(JSON.stringify({
    code: 'configuration_invalid', detail: 'Configuration validation failed.', trace_id: 'trace-pack-validation',
    diagnostics: [{ ...diagnostic, private_extension: 'drop-this' }], private_extension: 'drop-this',
  }), { status: 400, headers: { 'content-type': 'application/problem+json' } })) as typeof fetch;
  for (const [method, path, body] of [
    ['POST', '/api/v1/agent-packs/install-preview', { api_version: 'tinadec.io/agent-pack/v1alpha1' }],
    ['PUT', '/api/v1/agent-packs/tinadec.graph.seed-pack', { preview_id: 'preview-1', envelope: {} }],
  ] as const) {
    const response = await app.handle(new Request(`http://gateway.local${path}`, {
      method, headers: { 'content-type': 'application/json' }, body: JSON.stringify(body),
    }));
    assert.equal(response.status, 400);
    assert.equal(response.headers.get('content-type'), 'application/problem+json');
    const problem = await response.json() as Record<string, unknown>;
    assert.equal(problem.code, 'configuration_invalid');
    assert.equal(problem.trace_id, 'trace-pack-validation');
    assert.deepEqual(problem.diagnostics, [diagnostic]);
    assert.equal(JSON.stringify(problem).includes('drop-this'), false);
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

test('invoke-stream proxy is retired and never reaches Core', { concurrency: false }, async () => {
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

  // The legacy wire is gone end to end (plan §4.3 item 4): Core deleted the
  // route, the Gateway proxy is removed with it. Submissions go through
  // POST /sessions/{id}/interactions + GET /runs/{runId}/stream.
  assert.notEqual(response.status, 200);
  assert.equal(forwarded, undefined);
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
  assert.equal(forwarded?.url, 'http://127.0.0.1:48731/api/v1/tools/command_run/execute');
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
    return new Response('provider-denied: raw body', {
      status: 422,
      headers: { 'content-type': 'text/plain; charset=utf-8', etag: '"provider-4"' },
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
  assert.equal(response.headers.get('content-type'), 'text/plain; charset=utf-8');
  assert.equal(response.headers.get('etag'), '"provider-4"');
  assert.equal(await response.text(), 'provider-denied: raw body');
  assert.equal(forwarded?.url, 'http://127.0.0.1:48731/api/v1/tools/read_file/execute');
  assert.equal(forwarded?.method, 'POST');
  assert.deepEqual(JSON.parse(forwarded?.body ?? ''), body);
  assert.equal(forwarded?.headers.get('x-request-id'), 'request-direct-1');
  assert.equal(forwarded?.headers.get('x-tenant-id'), 'tenant-1');
});

test('approval pre-authorization creation is a stateless Core proxy', { concurrency: false }, async () => {
  const requests: Array<{ url: string; method: string; body: string | undefined }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({
      url: String(input),
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : undefined,
    });
    return new Response(JSON.stringify({
      id: 'pre-auth-1',
      run_id: 'run-1',
      lane_key: null,
      tool_scope: ['write_file'],
      parameter_constraint_hash: null,
      risk_max: 'medium',
      max_uses: 2,
      use_count: 0,
      expires_at: '2026-09-16T00:00:00Z',
      revoked: false,
    }), { status: 201, headers: { 'content-type': 'application/json' } });
  }) as typeof fetch;

  const body = { run_id: 'run-1', tool_scope: ['write_file'], risk_max: 'medium', max_uses: 2, summary: 'unattended commit lane' };
  const response = await app.handle(new Request('http://gateway.local/api/v1/approvals/pre-authorizations', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  }));

  assert.equal(response.status, 201);
  assert.deepEqual(await response.json(), {
    id: 'pre-auth-1',
    run_id: 'run-1',
    lane_key: null,
    tool_scope: ['write_file'],
    parameter_constraint_hash: null,
    risk_max: 'medium',
    max_uses: 2,
    use_count: 0,
    expires_at: '2026-09-16T00:00:00Z',
    revoked: false,
  });
  assert.deepEqual(requests.map(({ method, url }) => [method, url]), [
    ['POST', 'http://127.0.0.1:48731/api/v1/approvals/pre-authorizations'],
  ]);
  assert.deepEqual(JSON.parse(requests[0]!.body ?? ''), body);
});

test('approval pre-authorization creation surfaces Core validation as ProblemDetails', { concurrency: false }, async () => {
  globalThis.fetch = (async () => new Response(JSON.stringify({
    type: 'https://tinadec.dev/errors/invalid_request',
    title: 'Invalid request',
    status: 400,
    detail: 'Wildcard tool grants are not allowed in a pre-authorization.',
    code: 'invalid_request',
    trace_id: 'trace-pre-auth-1',
  }), { status: 400, headers: { 'content-type': 'application/problem+json' } })) as typeof fetch;

  const response = await app.handle(new Request('http://gateway.local/api/v1/approvals/pre-authorizations', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ run_id: 'run-1', tool_scope: ['*'], max_uses: 1 }),
  }));

  assert.equal(response.status, 400);
  assert.equal(response.headers.get('content-type'), 'application/problem+json');
  const problem = await response.json() as Record<string, unknown>;
  assert.equal(problem.code, 'invalid_request');
  assert.equal(problem.instance, '/api/v1/approvals/pre-authorizations');
  assert.equal(problem.trace_id, 'trace-pre-auth-1');
});

test('memory item review routes stay stateless Core proxies', { concurrency: false }, async () => {
  const requests: Array<{ url: string; method: string; body: string | undefined }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({
      url: String(input),
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : undefined,
    });
    return new Response(JSON.stringify({ proxied: true }), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    });
  }) as typeof fetch;

  const responses = [
    // session_id is not a candidate filter (a candidate names its run, not its
    // session), so it must not reach Core at all.
    new Request('http://gateway.local/api/v1/memory-candidates?status=proposed&scope=workspace&run_id=run-1&session_id=session-1'),
    new Request('http://gateway.local/api/v1/memory-items?status=active&kind=fact&limit=20'),
    new Request('http://gateway.local/api/v1/memory-items/item-1/revoke', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({}),
    }),
  ];
  for (const request of responses) {
    const response = await app.handle(request);
    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), { proxied: true });
  }

  assert.deepEqual(requests.map(({ method, url }) => [method, url]), [
    ['GET', 'http://127.0.0.1:48731/api/v1/memory-candidates?status=proposed&scope=workspace&run_id=run-1'],
    ['GET', 'http://127.0.0.1:48731/api/v1/memory-items?status=active&kind=fact&limit=20'],
    ['POST', 'http://127.0.0.1:48731/api/v1/memory-items/item-1/revoke'],
  ]);
});

// Core names review failures with its own UPPER_SNAKE codes. Left out of the
// mapper they all fall through to the default, which turns "you sent a value this
// queue cannot use" into the one code that means "try again later".
test('memory review failures keep their 4xx meaning instead of collapsing to conflict', { concurrency: false }, async () => {
  const cases: Array<{ method: string; path: string; coreCode: string; status: number; expected: string }> = [
    { method: 'GET', path: '/api/v1/memory-candidates?run_id=yesterday', coreCode: 'INVALID_RUN_ID', status: 400, expected: 'invalid_request' },
    { method: 'POST', path: '/api/v1/memory-candidates/candidate-1/promote', coreCode: 'INVALID_DECISION', status: 400, expected: 'invalid_request' },
    { method: 'POST', path: '/api/v1/memory-candidates/candidate-1/reject', coreCode: 'ALREADY_DECIDED', status: 409, expected: 'conflict' },
    { method: 'POST', path: '/api/v1/memory-items/item-1/revoke', coreCode: 'NOT_FOUND', status: 404, expected: 'not_found' },
  ];

  for (const testCase of cases) {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      code: testCase.coreCode,
      message: 'review refused',
      field: 'run_id',
    }), { status: testCase.status, headers: { 'content-type': 'application/json' } })) as typeof fetch;

    const response = await app.handle(new Request(`http://gateway.local${testCase.path}`, {
      method: testCase.method,
      headers: { 'content-type': 'application/json' },
      body: testCase.method === 'POST' ? JSON.stringify({ reason: 'because' }) : undefined,
    }));

    assert.equal(response.status, testCase.status, testCase.path);
    assert.equal(response.headers.get('content-type'), 'application/problem+json');
    const problem = await response.json() as Record<string, unknown>;
    assert.equal(problem.code, testCase.expected, `${testCase.path} (${testCase.coreCode})`);
    assert.equal(problem.detail, 'review refused');
    assert.equal(problem.instance, testCase.path.split('?')[0]);
  }
});

test('session tool executions forward run_id and limit under their snake_case names', { concurrency: false }, async () => {
  let url = '';
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    url = String(input);
    return new Response('[]', { headers: { 'content-type': 'application/json' } });
  }) as typeof fetch;
  const response = await app.handle(new Request('http://gateway.local/api/v1/sessions/s-1/tool-executions?run_id=r-1&limit=20'));
  assert.equal(response.status, 200);
  const forwarded = new URL(url);
  assert.equal(forwarded.pathname, '/api/v1/sessions/s-1/tool-executions');
  assert.equal(forwarded.searchParams.get('run_id'), 'r-1');
  assert.equal(forwarded.searchParams.get('runId'), null);
  assert.equal(forwarded.searchParams.get('limit'), '20');
});

test('tool execution preview is a no-store passthrough that keeps Core problem codes', { concurrency: false }, async () => {
  const body = { execution_id: 'e-1', run_id: 'r-1', tool_id: 'read_file', status: 'completed', arguments: { filepath: 'a.ts' }, preview: { kind: 'file_lines', lines: [{ line_number: 1, text: 'x' }] }, truncated: false };
  let url = '';
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    url = String(input);
    return new Response(JSON.stringify(body), { headers: { 'content-type': 'application/json' } });
  }) as typeof fetch;
  const ok = await app.handle(new Request('http://gateway.local/api/v1/tool-executions/e-1/preview?max_bytes=4096'));
  assert.equal(ok.status, 200);
  assert.equal(ok.headers.get('cache-control'), 'no-store');
  assert.deepEqual(await ok.json(), body);
  assert.equal(new URL(url).pathname, '/api/v1/tool-executions/e-1/preview');
  assert.equal(new URL(url).searchParams.get('max_bytes'), '4096');

  await app.handle(new Request('http://gateway.local/api/v1/tool-executions/e-1/preview'));
  assert.equal(new URL(url).search, '');

  globalThis.fetch = (async () => new Response(JSON.stringify({ code: 'tool_execution_not_found', detail: 'Tool execution was not found.' }), { status: 404, headers: { 'content-type': 'application/problem+json' } })) as typeof fetch;
  const missing = await app.handle(new Request('http://gateway.local/api/v1/tool-executions/e-2/preview'));
  assert.equal(missing.status, 404);
  assert.equal(missing.headers.get('content-type'), 'application/problem+json');
  assert.equal(missing.headers.get('cache-control'), 'no-store');
  const problem = await missing.json() as Record<string, unknown>;
  assert.equal(problem.code, 'tool_execution_not_found');
  assert.equal(problem.instance, '/api/v1/tool-executions/e-2/preview');
});
