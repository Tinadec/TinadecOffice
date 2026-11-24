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
    ['/api/v1/projects/{projectId}/board/settings', ['get', 'put']],
    ['/api/v1/projects/{projectId}/board/sync', ['post']],
    ['/api/v1/projects/{projectId}/board/tasks', ['get', 'post']],
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
  ]) {
    assert.ok(doc.components.schemas[schemaName], `Missing OpenAPI schema ${schemaName}`);
  }

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
