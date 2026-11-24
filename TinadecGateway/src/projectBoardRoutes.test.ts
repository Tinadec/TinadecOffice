import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, test } from 'node:test';
import { app } from './index.js';
import { mapSession } from './mappers/sessionMapper.js';
import { wrapSseWithKeepalive } from './streaming.js';

const originalFetch = globalThis.fetch;
afterEach(() => {
  globalThis.fetch = originalFetch;
});

const projectId = '11111111-1111-4111-8111-111111111111';
const taskId = '22222222-2222-4222-8222-222222222222';
const viewId = '33333333-3333-4333-8333-333333333333';

test('Session mapper and /api/v1/sessions preserve origin and board_task_id', { concurrency: false }, async () => {
  const mappedBoardSession = mapSession({
    id: '44444444-4444-4444-8444-444444444444',
    project_id: projectId,
    title: 'TIN-7: Implement sync engine',
    origin: 'board_task',
    board_task_id: taskId,
  });
  assert.ok(mappedBoardSession);
  assert.equal(mappedBoardSession.origin, 'board_task');
  assert.equal(mappedBoardSession.board_task_id, taskId);

  const mappedCamelCase = mapSession({
    id: '55555555-5555-4555-8555-555555555555',
    origin: 'board_task',
    boardTaskId: taskId,
  });
  assert.ok(mappedCamelCase);
  assert.equal(mappedCamelCase.origin, 'board_task');
  assert.equal(mappedCamelCase.board_task_id, taskId);

  const mappedRegularSession = mapSession({ id: '66666666-6666-4666-8666-666666666666' });
  assert.ok(mappedRegularSession);
  assert.equal(mappedRegularSession.origin, null);
  assert.equal(mappedRegularSession.board_task_id, null);

  const capturedBodies: Array<Record<string, unknown>> = [];
  globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
    if (init?.method === 'POST') {
      capturedBodies.push(JSON.parse(String(init.body)));
      return new Response(
        JSON.stringify({
          id: '44444444-4444-4444-8444-444444444444',
          project_id: projectId,
          title: 'TIN-7',
          origin: 'board_task',
          board_task_id: taskId,
        }),
        { status: 201, headers: { 'content-type': 'application/json' } },
      );
    }
    return new Response(
      JSON.stringify([
        {
          id: '44444444-4444-4444-8444-444444444444',
          project_id: projectId,
          origin: 'board_task',
          board_task_id: taskId,
        },
        {
          id: '66666666-6666-4666-8666-666666666666',
          project_id: projectId,
        },
      ]),
      { status: 200, headers: { 'content-type': 'application/json' } },
    );
  }) as unknown as typeof fetch;

  const created = await app.handle(
    new Request('http://gateway.local/api/v1/sessions', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        project_id: projectId,
        title: 'TIN-7',
        origin: 'board_task',
        board_task_id: taskId,
      }),
    }),
  );
  assert.equal(created.status, 201);
  assert.equal(capturedBodies[0]?.origin, 'board_task');
  assert.equal(capturedBodies[0]?.board_task_id, taskId);
  const createdJson = (await created.json()) as Record<string, unknown>;
  assert.equal(createdJson.origin, 'board_task');
  assert.equal(createdJson.board_task_id, taskId);

  const listed = await app.handle(
    new Request(`http://gateway.local/api/v1/sessions?project_id=${projectId}`),
  );
  assert.equal(listed.status, 200);
  const listedJson = (await listed.json()) as Array<Record<string, unknown>>;
  assert.equal(listedJson[0]?.origin, 'board_task');
  assert.equal(listedJson[0]?.board_task_id, taskId);
  assert.equal(listedJson[1]?.origin, null);
  assert.equal(listedJson[1]?.board_task_id, null);
});

test('Project Board and Global Board REST routes forward paths, queries, headers, bodies, and 204 status verbatim', { concurrency: false }, async () => {
  const calls: Array<{ path: string; method: string; body: string | undefined; headers: Headers }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    const method = init?.method ?? 'GET';
    calls.push({
      path: url.pathname + url.search,
      method,
      body: typeof init?.body === 'string' ? init.body : undefined,
      headers: new Headers(init?.headers),
    });

    if (method === 'DELETE') {
      return new Response(null, { status: 204 });
    }

    return new Response(JSON.stringify({ ok: true, path: url.pathname }), {
      status: method === 'POST' && (url.pathname.endsWith('/tasks') || url.pathname.endsWith('/views')) ? 201 : 200,
      headers: { 'content-type': 'application/json', etag: '"rev-3"' },
    });
  }) as unknown as typeof fetch;

  const cases: Array<{
    method: string;
    path: string;
    body?: Record<string, unknown>;
    expectedStatus: number;
  }> = [
    { method: 'GET', path: `/api/v1/projects/${projectId}/board/settings`, expectedStatus: 200 },
    {
      method: 'PUT',
      path: `/api/v1/projects/${projectId}/board/settings`,
      body: { key_prefix: 'TIN', storage_mode: 'repo_tinadec', max_concurrency: 2 },
      expectedStatus: 200,
    },
    { method: 'POST', path: `/api/v1/projects/${projectId}/board/sync`, expectedStatus: 200 },
    {
      method: 'GET',
      path: `/api/v1/projects/${projectId}/board/tasks?status=in_progress&assignee_kind=cli&has_conflict=true`,
      expectedStatus: 200,
    },
    {
      method: 'POST',
      path: `/api/v1/projects/${projectId}/board/tasks`,
      body: { title: 'Wire board proxy', priority: 'high', labels: ['gateway'] },
      expectedStatus: 201,
    },
    {
      method: 'POST',
      path: `/api/v1/projects/${projectId}/board/tasks/promote-from-spatial`,
      body: {
        session_id: '77777777-7777-4777-8777-777777777777',
        object_ids: ['sticky:1', 'sticky:2'],
        mode: 'split_many',
        target_status: 'ready',
        priority: 'high',
        labels: ['spatial'],
      },
      expectedStatus: 200,
    },
    { method: 'GET', path: `/api/v1/projects/${projectId}/board/tasks/${taskId}`, expectedStatus: 200 },
    {
      method: 'PATCH',
      path: `/api/v1/projects/${projectId}/board/tasks/${taskId}`,
      body: { status: 'in_review', sort_key: 'a0', expected_revision: 2 },
      expectedStatus: 200,
    },
    {
      method: 'POST',
      path: `/api/v1/projects/${projectId}/board/tasks/${taskId}/resolve-conflict`,
      body: { resolution: 'keep_file' },
      expectedStatus: 200,
    },
    {
      method: 'POST',
      path: `/api/v1/projects/${projectId}/board/tasks/${taskId}/dispatch`,
      body: { use_worktree: true, assignee_override: { kind: 'cli', harness_id: 'claude-code' } },
      expectedStatus: 200,
    },
    { method: 'GET', path: `/api/v1/projects/${projectId}/board/tasks/${taskId}/diff`, expectedStatus: 200 },
    {
      method: 'POST',
      path: `/api/v1/projects/${projectId}/board/tasks/${taskId}/approve`,
      body: { merge_or_clean_worktree: true, note: 'LGTM' },
      expectedStatus: 200,
    },
    {
      method: 'POST',
      path: `/api/v1/projects/${projectId}/board/tasks/${taskId}/request-changes`,
      body: { feedback: 'Add conflict test coverage' },
      expectedStatus: 200,
    },
    { method: 'POST', path: `/api/v1/projects/${projectId}/board/tasks/${taskId}/cancel`, expectedStatus: 200 },
    { method: 'DELETE', path: `/api/v1/projects/${projectId}/board/tasks/${taskId}`, expectedStatus: 204 },
    {
      method: 'GET',
      path: `/api/v1/board/tasks?project_ids=${projectId}&needs_my_action=true&query=sync`,
      expectedStatus: 200,
    },
    { method: 'GET', path: '/api/v1/board/summary', expectedStatus: 200 },
    {
      method: 'GET',
      path: `/api/v1/board/views?scope=project&project_id=${projectId}`,
      expectedStatus: 200,
    },
    {
      method: 'POST',
      path: '/api/v1/board/views',
      body: {
        scope: 'global',
        name: 'Swimlane by Project',
        config: {
          layout: 'swimlane',
          group_by: 'status',
          swimlane_by: 'project',
          sort_by: 'manual',
          filters: { needs_my_action: true },
          visible_fields: ['live_digest', 'diff_stats'],
        },
      },
      expectedStatus: 201,
    },
    {
      method: 'PATCH',
      path: `/api/v1/board/views/${viewId}`,
      body: { name: 'Updated View', sort_order: 1 },
      expectedStatus: 200,
    },
    { method: 'DELETE', path: `/api/v1/board/views/${viewId}`, expectedStatus: 204 },
  ];

  for (const item of cases) {
    const res = await app.handle(
      new Request(`http://gateway.local${item.path}`, {
        method: item.method,
        headers: {
          'x-tinadec-storage-id': 'scope-board-1',
          'if-match': '"rev-2"',
          'idempotency-key': 'idem-board-1',
          ...(item.body ? { 'content-type': 'application/json' } : {}),
        },
        body: item.body ? JSON.stringify(item.body) : undefined,
      }),
    );

    assert.equal(res.status, item.expectedStatus, `${item.method} ${item.path}`);
    const last = calls.at(-1)!;
    assert.equal(last.method, item.method);
    assert.equal(last.path, item.path);
    assert.equal(last.headers.get('x-tinadec-storage-id'), 'scope-board-1');
    assert.equal(last.headers.get('if-match'), '"rev-2"');
    assert.equal(last.headers.get('idempotency-key'), 'idem-board-1');
    if (item.body) {
      assert.deepEqual(JSON.parse(last.body ?? '{}'), item.body);
    }
  }

  // Bodyless POST on optional-body routes (dispatch / approve) sends '{}' so Core Minimal API deserializes cleanly.
  const emptyDispatch = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/board/tasks/${taskId}/dispatch`, {
      method: 'POST',
    }),
  );
  assert.equal(emptyDispatch.status, 200);
  assert.equal(calls.at(-1)!.body, '{}');
});

test('Project and Global Board SSE routes stream unbuffered events, forward cursors, and emit keepalives', { concurrency: false }, async () => {
  for (const ssePath of [
    `/api/v1/projects/${projectId}/board/events`,
    '/api/v1/board/events',
  ]) {
    let upstreamPath = '';
    let upstreamHeaders = new Headers();
    let releaseSecondChunk!: () => void;
    const secondChunkGate = new Promise<void>((resolveDone) => {
      releaseSecondChunk = resolveDone;
    });

    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input));
      upstreamPath = url.pathname + url.search;
      upstreamHeaders = new Headers(init?.headers);

      const encoder = new TextEncoder();
      const stream = new ReadableStream<Uint8Array>({
        async start(controller) {
          controller.enqueue(
            encoder.encode('id: 12\nevent: task_upserted\ndata: {"seq":12,"event_type":"task_upserted"}\n\n'),
          );
          await secondChunkGate;
          controller.enqueue(
            encoder.encode('id: 13\nevent: sync_completed\ndata: {"seq":13,"event_type":"sync_completed"}\n\n'),
          );
          controller.close();
        },
      });

      return new Response(stream, {
        status: 200,
        headers: { 'content-type': 'text/event-stream' },
      });
    }) as unknown as typeof fetch;

    const response = await app.handle(
      new Request(`http://gateway.local${ssePath}?storage_id=scope-sse`, {
        headers: { 'Last-Event-ID': '11' },
      }),
    );

    assert.equal(response.status, 200);
    assert.match(response.headers.get('content-type') ?? '', /text\/event-stream/);
    assert.equal(response.headers.get('cache-control'), 'no-cache, no-transform');
    assert.equal(response.headers.get('x-accel-buffering'), 'no');
    assert.equal(upstreamPath, `${ssePath}?storage_id=scope-sse&after_seq=11`);
    assert.equal(upstreamHeaders.get('last-event-id'), '11');
    assert.equal(upstreamHeaders.get('x-tinadec-storage-id'), 'scope-sse');

    const reader = response.body!.getReader();
    const decoder = new TextDecoder();

    const first = await reader.read();
    assert.equal(first.done, false);
    assert.match(decoder.decode(first.value), /event: task_upserted/);

    releaseSecondChunk();
    let rest = '';
    while (true) {
      const next = await reader.read();
      if (next.done) break;
      rest += decoder.decode(next.value, { stream: true });
    }
    assert.match(rest, /event: sync_completed/);
  }

  // Verify wrapSseWithKeepalive emits `: keepalive\n\n` on an idle stream cleanly.
  const idleSource = new ReadableStream<Uint8Array>({
    start() {
      // Stays idle until cancelled by abort signal.
    },
  });
  const abortController = new AbortController();
  const wrapped = wrapSseWithKeepalive(idleSource, 15, abortController.signal)!;
  const keepaliveReader = wrapped.getReader();
  const chunk = await keepaliveReader.read();
  assert.equal(chunk.done, false);
  assert.equal(new TextDecoder().decode(chunk.value), ': keepalive\n\n');
  abortController.abort();
});

test('Project Board routes preserve Core 409 cycle_detected/revision_conflict and 502 CORE_UNREACHABLE', { concurrency: false }, async () => {
  globalThis.fetch = (async () =>
    new Response(
      JSON.stringify({
        type: 'https://tinadec.dev/problems/cycle_detected',
        title: 'Dependency cycle detected',
        status: 409,
        code: 'cycle_detected',
        detail: 'Task TIN-3 cannot depend on TIN-1 because TIN-1 already depends on TIN-3.',
      }),
      { status: 409, headers: { 'content-type': 'application/problem+json' } },
    )) as unknown as typeof fetch;

  const cycleRes = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/board/tasks/${taskId}`, {
      method: 'PATCH',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ depends_on: ['99999999-9999-4999-8999-999999999999'] }),
    }),
  );
  assert.equal(cycleRes.status, 409);
  const cycleBody = (await cycleRes.json()) as Record<string, unknown>;
  assert.equal(cycleBody.code, 'cycle_detected');

  globalThis.fetch = (async () =>
    new Response(
      JSON.stringify({
        status: 409,
        code: 'revision_conflict',
        detail: 'Expected revision 4, actual revision 5.',
      }),
      { status: 409, headers: { 'content-type': 'application/problem+json' } },
    )) as unknown as typeof fetch;

  const revConflict = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/board/tasks/${taskId}`, {
      method: 'PATCH',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ title: 'Stale write', expected_revision: 4 }),
    }),
  );
  assert.equal(revConflict.status, 409);
  assert.equal(((await revConflict.json()) as Record<string, unknown>).code, 'revision_conflict');

  globalThis.fetch = (async () => {
    throw new TypeError('connect ECONNREFUSED 127.0.0.1:48731');
  }) as unknown as typeof fetch;

  const restUnreachable = await app.handle(
    new Request('http://gateway.local/api/v1/board/summary'),
  );
  assert.equal(restUnreachable.status, 502);
  assert.equal(((await restUnreachable.json()) as Record<string, unknown>).code, 'CORE_UNREACHABLE');

  const sseUnreachable = await app.handle(
    new Request('http://gateway.local/api/v1/board/events'),
  );
  assert.equal(sseUnreachable.status, 502);
  assert.equal(((await sseUnreachable.json()) as Record<string, unknown>).code, 'CORE_UNREACHABLE');
});

test('POST /api/v1/projects/:projectId/board/tasks/promote-from-spatial forwards 200 result and preserves 400/404/409/502 errors', { concurrency: false }, async () => {
  const spatialSessionId = '77777777-7777-4777-8777-777777777777';
  let capturedPath = '';
  let capturedMethod = '';
  let capturedHeaders = new Headers();
  let capturedBody: Record<string, unknown> | null = null;

  const mockResult = {
    mode: 'parent_with_children',
    parent_task_id: taskId,
    tasks: [
      {
        task_id: taskId,
        project_id: projectId,
        project_name: 'TinadecOffice',
        task_key: 'TIN-14',
        title: 'Spatial Bridge Epic',
        description_md: 'Promoted from spatial section',
        checklist: [],
        status: 'ready',
        priority: 'high',
        blocked: false,
        blocked_reason: null,
        assignee: { kind: 'cli', harness_id: 'claude-code' },
        parent_task_id: null,
        depends_on: [],
        labels: ['spatial'],
        spatial_ref: { session_id: spatialSessionId, object_id: 'section:1', viewport_hint: { x: 120, y: 80, zoom: 1 } },
        sort_key: 'a0',
        active_session_id: null,
        active_run_id: null,
        run_status: null,
        worktree_path: null,
        lease_id: null,
        live_digest_summary: null,
        diff_stats: null,
        has_sync_conflict: false,
        sync_conflict: null,
        revision: 1,
        created_at_unix_ms: 1700000000000,
        updated_at_unix_ms: 1700000000000,
      },
    ],
    updated_object_links: [
      { object_id: 'section:1', task_id: taskId, task_key: 'TIN-14' },
    ],
    spatial_seq: 42,
  };

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    capturedPath = url.pathname + url.search;
    capturedMethod = init?.method ?? 'GET';
    capturedHeaders = new Headers(init?.headers);
    capturedBody = init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : null;
    return new Response(JSON.stringify(mockResult), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    });
  }) as unknown as typeof fetch;

  const promotePayload = {
    session_id: spatialSessionId,
    object_ids: ['section:1', 'sticky:1'],
    mode: 'parent_with_children',
    target_status: 'ready',
    priority: 'high',
    assignee: { kind: 'cli', harness_id: 'claude-code' },
    labels: ['spatial'],
    viewport_hint: { x: 120, y: 80, zoom: 1 },
    writer_id: 'desktop_promoter',
  };

  const okRes = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/board/tasks/promote-from-spatial`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-tinadec-storage-id': 'scope-spatial-bridge',
      },
      body: JSON.stringify(promotePayload),
    }),
  );

  assert.equal(okRes.status, 200);
  assert.equal(capturedMethod, 'POST');
  assert.equal(capturedPath, `/api/v1/projects/${projectId}/board/tasks/promote-from-spatial`);
  assert.equal(capturedHeaders.get('x-tinadec-storage-id'), 'scope-spatial-bridge');
  assert.deepEqual(capturedBody, promotePayload);
  const okJson = (await okRes.json()) as Record<string, unknown>;
  assert.deepEqual(okJson, mockResult);

  // 400 validation pass-through from Core
  globalThis.fetch = (async () =>
    new Response(
      JSON.stringify({
        type: 'https://tinadec.dev/problems/invalid_promotion_request',
        title: 'Invalid spatial promotion request',
        status: 400,
        code: 'invalid_promotion_mode',
        detail: 'Unsupported mode.',
      }),
      { status: 400, headers: { 'content-type': 'application/problem+json' } },
    )) as unknown as typeof fetch;

  const badRes = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/board/tasks/promote-from-spatial`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ session_id: spatialSessionId, object_ids: [] }),
    }),
  );
  assert.equal(badRes.status, 400);
  assert.equal(((await badRes.json()) as Record<string, unknown>).code, 'invalid_promotion_mode');

  // 404 missing spatial object pass-through
  globalThis.fetch = (async () =>
    new Response(
      JSON.stringify({
        status: 404,
        code: 'spatial_object_not_found',
        detail: 'Spatial object sticky:missing was not found.',
      }),
      { status: 404, headers: { 'content-type': 'application/problem+json' } },
    )) as unknown as typeof fetch;

  const notFoundRes = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/board/tasks/promote-from-spatial`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ session_id: spatialSessionId, object_ids: ['sticky:missing'] }),
    }),
  );
  assert.equal(notFoundRes.status, 404);
  assert.equal(((await notFoundRes.json()) as Record<string, unknown>).code, 'spatial_object_not_found');

  // Spatial "board" kind ops pass through cleanly (200 metadata-only & 400 board_props_invalid)
  globalThis.fetch = (async () =>
    new Response(
      JSON.stringify({
        seq: 43,
        accepted_op_ids: ['op:board:1'],
        rejected_ops: [],
        objects: {
          'board:1': {
            id: 'board:1',
            kind: 'board',
            parent_id: null,
            sort_key: 'a0',
            x: 0,
            y: 0,
            width: 780,
            height: 480,
            rotation: 0,
            props: { project_id: projectId, layout: 'kanban', compact: true },
            prop_timestamps: {},
            deleted: false,
            updated_at_unix_ms: 1700000001000,
          },
        },
      }),
      { status: 200, headers: { 'content-type': 'application/json' } },
    )) as unknown as typeof fetch;

  const boardOpRes = await app.handle(
    new Request(`http://gateway.local/api/v1/sessions/${spatialSessionId}/spatial-board/ops`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        client_id: 'desktop-1',
        base_seq: 42,
        ops: [
          {
            client_op_id: 'op:board:1',
            object_id: 'board:1',
            kind: 'board',
            props: { project_id: projectId, layout: 'kanban', compact: true },
          },
        ],
      }),
    }),
  );
  assert.equal(boardOpRes.status, 200);
  const boardOpJson = (await boardOpRes.json()) as { objects: Record<string, { kind: string }> };
  assert.equal(boardOpJson.objects['board:1']?.kind, 'board');

  globalThis.fetch = (async () =>
    new Response(
      JSON.stringify({
        status: 400,
        code: 'board_props_invalid',
        detail: 'Spatial board widgets must not embed task payloads in props.',
      }),
      { status: 400, headers: { 'content-type': 'application/problem+json' } },
    )) as unknown as typeof fetch;

  const invalidBoardPropsRes = await app.handle(
    new Request(`http://gateway.local/api/v1/sessions/${spatialSessionId}/spatial-board/ops`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        client_id: 'desktop-1',
        base_seq: 43,
        ops: [
          {
            client_op_id: 'op:board:bad',
            object_id: 'board:1',
            kind: 'board',
            props: { tasks: [{ id: 'bad-copy' }] },
          },
        ],
      }),
    }),
  );
  assert.equal(invalidBoardPropsRes.status, 400);
  assert.equal(((await invalidBoardPropsRes.json()) as Record<string, unknown>).code, 'board_props_invalid');
});

test('Phase 3 Project Notes, Coordinator Planner, and Autopilot routes forward requests and preserve 400/404/409/502 errors', { concurrency: false }, async () => {
  const calls: Array<{ path: string; method: string; body: Record<string, unknown> | null; headers: Headers }> = [];

  const mockNotes = {
    project_id: projectId,
    content_md: '# TinadecOffice Notes\n\n## 决策与进展记录\n- Initialized Phase 3',
    resolved_file_path: '/repo/.tinadec/project/notes.md',
    storage_mode: 'repo_tinadec',
    revision: 3,
    last_synced_hash: 'sha256:abc123',
    updated_at_unix_ms: 1700000010000,
  };

  const mockDraft = {
    draft_id: 'draft-1',
    title: 'Implement living notes sync',
    description_md: 'Wire bidirectional `.tinadec/project/notes.md` watcher',
    checklist: [{ id: 'chk-1', text: 'SHA-256 anti-echo', done: false }],
    priority: 'high',
    status: 'ready',
    assignee: { kind: 'native', id: 'coder' },
    labels: ['autopilot'],
    depends_on_draft_ids: [],
    depends_on_task_ids: [],
  };

  const mockTask = {
    task_id: taskId,
    project_id: projectId,
    project_name: 'TinadecOffice',
    task_key: 'TIN-21',
    title: 'Implement living notes sync',
    description_md: 'Wire bidirectional `.tinadec/project/notes.md` watcher',
    checklist: [{ id: 'chk-1', text: 'SHA-256 anti-echo', done: false }],
    status: 'ready',
    priority: 'high',
    blocked: false,
    blocked_reason: null,
    assignee: { kind: 'native', id: 'coder' },
    parent_task_id: null,
    depends_on: [],
    labels: ['autopilot'],
    spatial_ref: null,
    sort_key: 'a0',
    active_session_id: null,
    active_run_id: null,
    run_status: null,
    worktree_path: null,
    lease_id: null,
    live_digest_summary: null,
    diff_stats: null,
    has_sync_conflict: false,
    sync_conflict: null,
    revision: 1,
    created_at_unix_ms: 1700000010000,
    updated_at_unix_ms: 1700000010000,
  };

  const mockPlanResult = {
    project_id: projectId,
    goal: 'Ship living notes and coordinator autopilot',
    summary: 'Decomposed into 1 actionable task',
    planner_source: 'heuristic',
    auto_applied: false,
    drafts: [mockDraft],
    created_tasks: [],
  };

  const mockApplyPlanResult = {
    project_id: projectId,
    created_tasks: [mockTask],
  };

  const mockAutopilotState = {
    project_id: projectId,
    autopilot_enabled: true,
    max_concurrency: 2,
    active_task_count: 1,
    active_task_ids: [taskId],
    queued_ready_count: 2,
    blocked_task_count: 0,
    blocked_tasks: [],
    last_tick_at_unix_ms: 1700000015000,
    last_error: null,
    updated_at_unix_ms: 1700000015000,
  };

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    const method = init?.method ?? 'GET';
    calls.push({
      path: url.pathname + url.search,
      method,
      body: typeof init?.body === 'string' && init.body.length > 0 ? (JSON.parse(init.body) as Record<string, unknown>) : null,
      headers: new Headers(init?.headers),
    });

    if (url.pathname.endsWith('/notes')) {
      return new Response(JSON.stringify(mockNotes), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      });
    }
    if (url.pathname.endsWith('/coordinator/plan')) {
      return new Response(JSON.stringify(mockPlanResult), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      });
    }
    if (url.pathname.endsWith('/coordinator/apply-plan')) {
      return new Response(JSON.stringify(mockApplyPlanResult), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      });
    }
    if (url.pathname.endsWith('/coordinator/autopilot')) {
      return new Response(JSON.stringify(mockAutopilotState), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      });
    }

    return new Response(JSON.stringify({ ok: true }), { status: 200 });
  }) as unknown as typeof fetch;

  // 1. GET /api/v1/projects/:projectId/notes
  const getNotesRes = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/notes`, {
      headers: { 'x-tinadec-storage-id': 'scope-autopilot-56' },
    }),
  );
  assert.equal(getNotesRes.status, 200);
  assert.deepEqual(await getNotesRes.json(), mockNotes);
  assert.equal(calls.at(-1)?.method, 'GET');
  assert.equal(calls.at(-1)?.path, `/api/v1/projects/${projectId}/notes`);
  assert.equal(calls.at(-1)?.headers.get('x-tinadec-storage-id'), 'scope-autopilot-56');

  // 2. PUT /api/v1/projects/:projectId/notes
  const putNotesBody = { content_md: mockNotes.content_md, expected_revision: 2 };
  const putNotesRes = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/notes`, {
      method: 'PUT',
      headers: {
        'content-type': 'application/json',
        'x-tinadec-storage-id': 'scope-autopilot-56',
      },
      body: JSON.stringify(putNotesBody),
    }),
  );
  assert.equal(putNotesRes.status, 200);
  assert.deepEqual(await putNotesRes.json(), mockNotes);
  assert.equal(calls.at(-1)?.method, 'PUT');
  assert.deepEqual(calls.at(-1)?.body, putNotesBody);

  // 3. POST /api/v1/projects/:projectId/board/coordinator/plan
  const planBody = {
    goal: 'Ship living notes and coordinator autopilot',
    default_status: 'ready',
    default_assignee: { kind: 'native', id: 'coder' },
    auto_apply: false,
    max_tasks: 6,
  };
  const planRes = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/board/coordinator/plan`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-tinadec-storage-id': 'scope-autopilot-56',
      },
      body: JSON.stringify(planBody),
    }),
  );
  assert.equal(planRes.status, 200);
  assert.deepEqual(await planRes.json(), mockPlanResult);
  assert.equal(calls.at(-1)?.method, 'POST');
  assert.equal(calls.at(-1)?.path, `/api/v1/projects/${projectId}/board/coordinator/plan`);
  assert.deepEqual(calls.at(-1)?.body, planBody);

  // 4. POST /api/v1/projects/:projectId/board/coordinator/apply-plan
  const applyPlanBody = {
    goal: 'Ship living notes and coordinator autopilot',
    update_notes: true,
    drafts: [mockDraft],
  };
  const applyPlanRes = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/board/coordinator/apply-plan`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-tinadec-storage-id': 'scope-autopilot-56',
      },
      body: JSON.stringify(applyPlanBody),
    }),
  );
  assert.equal(applyPlanRes.status, 200);
  assert.deepEqual(await applyPlanRes.json(), mockApplyPlanResult);
  assert.equal(calls.at(-1)?.method, 'POST');
  assert.equal(calls.at(-1)?.path, `/api/v1/projects/${projectId}/board/coordinator/apply-plan`);
  assert.deepEqual(calls.at(-1)?.body, applyPlanBody);

  // 5. GET /api/v1/projects/:projectId/board/coordinator/autopilot
  const getAutopilotRes = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/board/coordinator/autopilot`, {
      headers: { 'x-tinadec-storage-id': 'scope-autopilot-56' },
    }),
  );
  assert.equal(getAutopilotRes.status, 200);
  assert.deepEqual(await getAutopilotRes.json(), mockAutopilotState);
  assert.equal(calls.at(-1)?.method, 'GET');
  assert.equal(calls.at(-1)?.path, `/api/v1/projects/${projectId}/board/coordinator/autopilot`);
  assert.equal(calls.at(-1)?.headers.get('x-tinadec-storage-id'), 'scope-autopilot-56');

  // 6. PUT /api/v1/projects/:projectId/board/coordinator/autopilot
  const putAutopilotBody = { autopilot_enabled: false, max_concurrency: 3, cancel_active: true };
  const putAutopilotRes = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/board/coordinator/autopilot`, {
      method: 'PUT',
      headers: {
        'content-type': 'application/json',
        'x-tinadec-storage-id': 'scope-autopilot-56',
      },
      body: JSON.stringify(putAutopilotBody),
    }),
  );
  assert.equal(putAutopilotRes.status, 200);
  assert.deepEqual(await putAutopilotRes.json(), mockAutopilotState);
  assert.equal(calls.at(-1)?.method, 'PUT');
  assert.deepEqual(calls.at(-1)?.body, putAutopilotBody);

  // 409 revision_conflict on PUT /notes
  globalThis.fetch = (async () =>
    new Response(
      JSON.stringify({
        status: 409,
        code: 'revision_conflict',
        detail: 'Expected notes revision 2, actual revision 3.',
      }),
      { status: 409, headers: { 'content-type': 'application/problem+json' } },
    )) as unknown as typeof fetch;

  const notesConflictRes = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/notes`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ content_md: 'stale edit', expected_revision: 2 }),
    }),
  );
  assert.equal(notesConflictRes.status, 409);
  assert.equal(((await notesConflictRes.json()) as Record<string, unknown>).code, 'revision_conflict');

  // 409 cycle_detected on POST /coordinator/apply-plan
  globalThis.fetch = (async () =>
    new Response(
      JSON.stringify({
        status: 409,
        code: 'cycle_detected',
        detail: 'Draft draft-1 and draft-2 form a dependency cycle.',
      }),
      { status: 409, headers: { 'content-type': 'application/problem+json' } },
    )) as unknown as typeof fetch;

  const applyCycleRes = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/board/coordinator/apply-plan`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        drafts: [
          { ...mockDraft, draft_id: 'draft-1', depends_on_draft_ids: ['draft-2'] },
          { ...mockDraft, draft_id: 'draft-2', depends_on_draft_ids: ['draft-1'] },
        ],
      }),
    }),
  );
  assert.equal(applyCycleRes.status, 409);
  assert.equal(((await applyCycleRes.json()) as Record<string, unknown>).code, 'cycle_detected');

  // 400 validation & 404 not found & 502 CORE_UNREACHABLE preservation on Phase 3 routes
  globalThis.fetch = (async () =>
    new Response(
      JSON.stringify({ status: 400, code: 'invalid_goal', detail: 'Goal must not be blank.' }),
      { status: 400, headers: { 'content-type': 'application/problem+json' } },
    )) as unknown as typeof fetch;

  const badPlanRes = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/board/coordinator/plan`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ goal: '   ' }),
    }),
  );
  assert.equal(badPlanRes.status, 400);
  assert.equal(((await badPlanRes.json()) as Record<string, unknown>).code, 'invalid_goal');

  globalThis.fetch = (async () =>
    new Response(
      JSON.stringify({ status: 404, code: 'project_not_found', detail: 'Project not found.' }),
      { status: 404, headers: { 'content-type': 'application/problem+json' } },
    )) as unknown as typeof fetch;

  const missingAutopilotRes = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/board/coordinator/autopilot`),
  );
  assert.equal(missingAutopilotRes.status, 404);
  assert.equal(((await missingAutopilotRes.json()) as Record<string, unknown>).code, 'project_not_found');

  globalThis.fetch = (async () => {
    throw new TypeError('connect ECONNREFUSED 127.0.0.1:48731');
  }) as unknown as typeof fetch;

  const unreachableNotesRes = await app.handle(
    new Request(`http://gateway.local/api/v1/projects/${projectId}/notes`),
  );
  assert.equal(unreachableNotesRes.status, 502);
  assert.equal(((await unreachableNotesRes.json()) as Record<string, unknown>).code, 'CORE_UNREACHABLE');
});

function sortKeysRecursively(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortKeysRecursively);
  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.keys(value as Record<string, unknown>)
        .sort()
        .map((key) => [key, sortKeysRecursively((value as Record<string, unknown>)[key])]),
    );
  }
  return value;
}

test('Project Board OpenAPI paths, schemas, Session origin/board_task_id, and snapshot stay in sync', { concurrency: false }, async () => {
  const res = await app.handle(new Request('http://gateway.local/docs/json'));
  assert.equal(res.status, 200);
  const doc = (await res.json()) as {
    paths: Record<string, Record<string, unknown>>;
    components: { schemas: Record<string, Record<string, unknown>> };
  };

  const expectedPaths: Array<[string, string[]]> = [
    ['/api/v1/projects/{projectId}/notes', ['get', 'put']],
    ['/api/v1/projects/{projectId}/board/settings', ['get', 'put']],
    ['/api/v1/projects/{projectId}/board/sync', ['post']],
    ['/api/v1/projects/{projectId}/board/coordinator/plan', ['post']],
    ['/api/v1/projects/{projectId}/board/coordinator/apply-plan', ['post']],
    ['/api/v1/projects/{projectId}/board/coordinator/autopilot', ['get', 'put']],
    ['/api/v1/projects/{projectId}/board/tasks', ['get', 'post']],
    ['/api/v1/projects/{projectId}/board/tasks/promote-from-spatial', ['post']],
    ['/api/v1/projects/{projectId}/board/tasks/{taskId}', ['get', 'patch', 'delete']],
    ['/api/v1/projects/{projectId}/board/tasks/{taskId}/resolve-conflict', ['post']],
    ['/api/v1/projects/{projectId}/board/tasks/{taskId}/dispatch', ['post']],
    ['/api/v1/projects/{projectId}/board/tasks/{taskId}/diff', ['get']],
    ['/api/v1/projects/{projectId}/board/tasks/{taskId}/approve', ['post']],
    ['/api/v1/projects/{projectId}/board/tasks/{taskId}/request-changes', ['post']],
    ['/api/v1/projects/{projectId}/board/tasks/{taskId}/cancel', ['post']],
    ['/api/v1/projects/{projectId}/board/events', ['get']],
    ['/api/v1/board/tasks', ['get']],
    ['/api/v1/board/summary', ['get']],
    ['/api/v1/board/events', ['get']],
    ['/api/v1/board/views', ['get', 'post']],
    ['/api/v1/board/views/{viewId}', ['patch', 'delete']],
  ];

  for (const [openApiPath, methods] of expectedPaths) {
    assert.ok(doc.paths[openApiPath], `Missing OpenAPI path ${openApiPath}`);
    for (const method of methods) {
      assert.ok(doc.paths[openApiPath]?.[method], `Missing ${method.toUpperCase()} on ${openApiPath}`);
    }
  }

  for (const schemaName of [
    'ProjectBoardSettings',
    'UpdateProjectBoardSettingsRequest',
    'ProjectBoardSyncResult',
    'ProjectBoardChecklistItem',
    'ProjectBoardAssignee',
    'ProjectBoardSpatialRef',
    'ProjectBoardDiffStats',
    'ProjectBoardTaskSnapshot',
    'ProjectBoardSyncConflict',
    'ProjectBoardTask',
    'CreateProjectBoardTaskRequest',
    'UpdateProjectBoardTaskRequest',
    'ResolveProjectBoardConflictRequest',
    'DispatchProjectBoardTaskRequest',
    'ProjectBoardDiffFile',
    'ProjectBoardTaskDiff',
    'ApproveProjectBoardTaskRequest',
    'RequestTaskChangesRequest',
    'ProjectBoardViewFilters',
    'ProjectBoardViewConfig',
    'ProjectBoardView',
    'CreateProjectBoardViewRequest',
    'UpdateProjectBoardViewRequest',
    'ProjectBoardProjectSummaryItem',
    'ProjectBoardSummary',
    'ProjectBoardEvent',
    'SpatialObjectKind',
    'SpatialLwwTimestamp',
    'SpatialBoardObject',
    'SpatialPropertyOp',
    'SpatialBoardOpsRequest',
    'SpatialBoardRejectedOp',
    'SpatialBoardOpsResponse',
    'SpatialBoardSnapshot',
    'PromoteFromSpatialRequest',
    'SpatialObjectTaskLink',
    'PromoteFromSpatialResult',
    'ProjectNotes',
    'UpdateProjectNotesRequest',
    'CoordinatorTaskDraft',
    'CoordinatorPlanRequest',
    'CoordinatorPlanResult',
    'CoordinatorApplyPlanRequest',
    'CoordinatorApplyPlanResult',
    'UpdateProjectAutopilotRequest',
    'ProjectAutopilotState',
  ]) {
    assert.ok(doc.components.schemas[schemaName], `Missing OpenAPI schema ${schemaName}`);
  }

  // Verify "board" spatial object kind is exposed in SpatialObjectKind, SpatialBoardObject, and SpatialPropertyOp.
  const spatialKindEnum = doc.components.schemas.SpatialObjectKind?.enum as string[] | undefined;
  assert.ok(spatialKindEnum?.includes('board'), 'SpatialObjectKind enum must include "board"');
  const boardObjProps = doc.components.schemas.SpatialBoardObject?.properties as Record<string, Record<string, unknown>>;
  assert.ok(
    (boardObjProps?.kind?.enum as string[] | undefined)?.includes('board'),
    'SpatialBoardObject.kind enum must include "board"',
  );
  const propOpProps = doc.components.schemas.SpatialPropertyOp?.properties as Record<string, Record<string, unknown>>;
  assert.ok(
    (propOpProps?.kind?.enum as string[] | undefined)?.includes('board'),
    'SpatialPropertyOp.kind enum must include "board"',
  );

  // Verify PromoteFromSpatialRequest / SpatialObjectTaskLink / PromoteFromSpatialResult wire contracts.
  assert.deepEqual(doc.components.schemas.PromoteFromSpatialRequest?.required, ['session_id', 'object_ids']);
  assert.deepEqual(doc.components.schemas.SpatialObjectTaskLink?.required, ['object_id', 'task_id', 'task_key']);
  assert.deepEqual(doc.components.schemas.PromoteFromSpatialResult?.required, [
    'mode',
    'parent_task_id',
    'tasks',
    'updated_object_links',
    'spatial_seq',
  ]);

  // Verify Phase 3 (#56) Notes, Coordinator Planner, Autopilot, and ProjectBoardEvent wire contracts.
  assert.deepEqual(doc.components.schemas.ProjectNotes?.required, [
    'project_id',
    'content_md',
    'resolved_file_path',
    'storage_mode',
    'revision',
    'last_synced_hash',
    'updated_at_unix_ms',
  ]);
  assert.deepEqual(doc.components.schemas.UpdateProjectNotesRequest?.required, ['content_md']);
  assert.deepEqual(doc.components.schemas.CoordinatorTaskDraft?.required, [
    'draft_id',
    'title',
    'description_md',
    'checklist',
    'priority',
    'status',
    'assignee',
    'labels',
    'depends_on_draft_ids',
    'depends_on_task_ids',
  ]);
  assert.deepEqual(doc.components.schemas.CoordinatorPlanRequest?.required, ['goal']);
  assert.deepEqual(doc.components.schemas.CoordinatorPlanResult?.required, [
    'project_id',
    'goal',
    'summary',
    'planner_source',
    'auto_applied',
    'drafts',
    'created_tasks',
  ]);
  assert.deepEqual(doc.components.schemas.CoordinatorApplyPlanRequest?.required, ['drafts']);
  assert.deepEqual(doc.components.schemas.CoordinatorApplyPlanResult?.required, ['project_id', 'created_tasks']);
  assert.deepEqual(doc.components.schemas.ProjectAutopilotState?.required, [
    'project_id',
    'autopilot_enabled',
    'max_concurrency',
    'active_task_count',
    'active_task_ids',
    'queued_ready_count',
    'blocked_task_count',
    'blocked_tasks',
    'last_tick_at_unix_ms',
    'last_error',
    'updated_at_unix_ms',
  ]);

  const boardEventProps = doc.components.schemas.ProjectBoardEvent?.properties as Record<string, Record<string, unknown>>;
  assert.ok(boardEventProps?.notes, 'ProjectBoardEvent.notes must be exposed in OpenAPI');
  assert.ok(boardEventProps?.autopilot, 'ProjectBoardEvent.autopilot must be exposed in OpenAPI');
  const boardEventTypeEnum = boardEventProps?.event_type?.enum as string[] | undefined;
  assert.ok(boardEventTypeEnum?.includes('notes_updated'), 'ProjectBoardEvent.event_type must include "notes_updated"');
  assert.ok(
    boardEventTypeEnum?.includes('autopilot_updated'),
    'ProjectBoardEvent.event_type must include "autopilot_updated"',
  );

  const sessionProps = doc.components.schemas.Session?.properties as Record<string, unknown>;
  const createSessionProps = doc.components.schemas.CreateSessionRequest?.properties as Record<string, unknown>;
  assert.ok(sessionProps?.origin, 'Session.origin must be exposed in OpenAPI');
  assert.ok(sessionProps?.board_task_id, 'Session.board_task_id must be exposed in OpenAPI');
  assert.ok(createSessionProps?.origin, 'CreateSessionRequest.origin must be exposed in OpenAPI');
  assert.ok(createSessionProps?.board_task_id, 'CreateSessionRequest.board_task_id must be exposed in OpenAPI');

  const snapshotPath = resolve(import.meta.dir, '../tests/__snapshots__/openapi.external.json');
  const committedSnapshot = readFileSync(snapshotPath, 'utf8').replace(/\r\n/g, '\n');
  const liveSerialized = JSON.stringify(sortKeysRecursively(doc), null, 2) + '\n';
  assert.equal(liveSerialized, committedSnapshot, 'openapi.external.json snapshot must match sorted /docs/json');
});
