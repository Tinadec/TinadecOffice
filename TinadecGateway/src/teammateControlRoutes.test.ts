import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;
afterEach(() => {
  globalThis.fetch = originalFetch;
});

const sampleRunId = '22222222-2222-2222-2222-222222222222';

test('GET /api/v1/runs/:runId/teammates proxies path, query and tenant/storage headers verbatim', { concurrency: false }, async () => {
  const calls: Array<{ path: string; method: string; headers: Headers }> = [];
  const upstreamPayload = {
    run_id: sampleRunId,
    session_id: '33333333-3333-3333-3333-333333333333',
    run_status: 'executing',
    teammates: [
      {
        handle: 'backend-api',
        agent_slug: 'global_engineering',
        mode: 'teammate',
        status: 'working',
        paused: false,
        awaiting_approval: false,
        current_task_id: '44444444-4444-4444-4444-444444444444',
        current_task_title: '@backend-api: Implement actor loop',
        claimed_step_id: 's1',
        latest_summary: 'Working on async actor scheduler',
        runtime_badge: 'API · gemini-4-carbon-high',
        worktree_path: null,
        branch_name: null,
        write_scope: ['src/**'],
        relay_depth: 1,
        pending_steer_count: 0,
        updated_at: '2026-11-23T02:30:00Z',
      },
    ],
  };

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    calls.push({
      path: url.pathname + url.search,
      method: init?.method ?? 'GET',
      headers: new Headers(init?.headers),
    });
    return new Response(JSON.stringify(upstreamPayload), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    });
  }) as typeof fetch;

  const res = await app.handle(
    new Request(`http://gateway.local/api/v1/runs/${sampleRunId}/teammates`, {
      method: 'GET',
      headers: {
        'x-request-id': 'req-tm-list',
        'x-tinadec-storage-id': 'project-alpha',
      },
    }),
  );

  assert.equal(res.status, 200);
  const body = (await res.json()) as typeof upstreamPayload;
  assert.equal(body.run_id, sampleRunId);
  assert.equal(body.teammates.length, 1);
  assert.equal(body.teammates[0]?.handle, 'backend-api');

  assert.equal(calls.length, 1);
  assert.equal(calls[0]!.path, `/api/v1/runs/${sampleRunId}/teammates`);
  assert.equal(calls[0]!.method, 'GET');
  assert.equal(calls[0]!.headers.get('x-request-id'), 'req-tm-list');
  assert.equal(calls[0]!.headers.get('x-tinadec-storage-id'), 'project-alpha');
});

test('POST /steer, /pause and /resume forward payload, encoded handle and Core error codes', { concurrency: false }, async () => {
  const calls: Array<{ path: string; method: string; body: string | undefined; headers: Headers }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    calls.push({
      path: url.pathname + url.search,
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : undefined,
      headers: new Headers(init?.headers),
    });

    if (url.pathname.endsWith('/steer')) {
      return new Response(
        JSON.stringify({
          accepted: true,
          run_id: sampleRunId,
          handle: 'backend-api',
          status: 'working',
          paused: false,
          urgent: true,
          woken: false,
          delivery_mode: 'injected_next_turn',
          board_seq: 7,
          pending_steer_count: 1,
          task_id: '44444444-4444-4444-4444-444444444444',
          delivered_at: '2026-11-23T02:31:00Z',
        }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      );
    }

    if (url.pathname.endsWith('/pause')) {
      return new Response(
        JSON.stringify({
          accepted: true,
          run_id: sampleRunId,
          handle: 'backend-api',
          action: 'pause',
          status: 'paused',
          paused: true,
          current_task_id: '44444444-4444-4444-4444-444444444444',
          updated_at: '2026-11-23T02:31:05Z',
        }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      );
    }

    if (url.pathname.endsWith('/resume') && url.pathname.includes('terminated-ephemeral')) {
      return new Response(
        JSON.stringify({
          code: 'TEAMMATE_TERMINATED',
          detail: 'Ephemeral teammate has already terminated.',
          trace_id: 'trace-409',
        }),
        { status: 409, headers: { 'content-type': 'application/problem+json' } },
      );
    }

    return new Response(
      JSON.stringify({
        accepted: true,
        run_id: sampleRunId,
        handle: 'backend-api',
        action: 'resume',
        status: 'working',
        paused: false,
        current_task_id: '44444444-4444-4444-4444-444444444444',
        updated_at: '2026-11-23T02:31:10Z',
      }),
      { status: 200, headers: { 'content-type': 'application/json' } },
    );
  }) as typeof fetch;

  // 1. Steer with urgent=true
  const steerPayload = { message: '请优先处理并发互斥锁', urgent: true };
  const steerRes = await app.handle(
    new Request(`http://gateway.local/api/v1/runs/${sampleRunId}/teammates/backend-api/steer`, {
      method: 'POST',
      body: JSON.stringify(steerPayload),
      headers: {
        'content-type': 'application/json',
        'x-request-id': 'req-steer-1',
        'x-tinadec-storage-id': 'ws-scope-9',
      },
    }),
  );
  assert.equal(steerRes.status, 200);
  const steerReceipt = (await steerRes.json()) as Record<string, unknown>;
  assert.equal(steerReceipt.delivery_mode, 'injected_next_turn');
  assert.equal(steerReceipt.urgent, true);
  assert.equal(calls.at(-1)?.headers.get('x-tinadec-storage-id'), 'ws-scope-9');
  assert.deepEqual(JSON.parse(calls.at(-1)?.body ?? '{}'), steerPayload);

  // 2. Pause (empty body allowed)
  const pauseRes = await app.handle(
    new Request(`http://gateway.local/api/v1/runs/${sampleRunId}/teammates/backend-api/pause`, {
      method: 'POST',
      headers: { 'x-request-id': 'req-pause-1' },
    }),
  );
  assert.equal(pauseRes.status, 200);
  const pauseReceipt = (await pauseRes.json()) as Record<string, unknown>;
  assert.equal(pauseReceipt.paused, true);
  assert.equal(pauseReceipt.action, 'pause');

  // 3. Resume active teammate
  const resumeRes = await app.handle(
    new Request(`http://gateway.local/api/v1/runs/${sampleRunId}/teammates/backend-api/resume`, {
      method: 'POST',
      headers: { 'x-request-id': 'req-resume-1' },
    }),
  );
  assert.equal(resumeRes.status, 200);
  const resumeReceipt = (await resumeRes.json()) as Record<string, unknown>;
  assert.equal(resumeReceipt.paused, false);
  assert.equal(resumeReceipt.action, 'resume');

  // 4. Conflict code preservation (409 TEAMMATE_TERMINATED)
  const conflictRes = await app.handle(
    new Request(`http://gateway.local/api/v1/runs/${sampleRunId}/teammates/terminated-ephemeral/resume`, {
      method: 'POST',
      headers: { 'x-request-id': 'req-resume-conflict' },
    }),
  );
  assert.equal(conflictRes.status, 409);
  const problem = (await conflictRes.json()) as Record<string, unknown>;
  assert.equal(problem.code, 'TEAMMATE_TERMINATED');
});

test('teammate control OpenAPI schemas and paths are exposed on /docs/json', async () => {
  const res = await app.handle(new Request('http://gateway.local/docs/json'));
  assert.equal(res.status, 200);
  const doc = (await res.json()) as {
    paths: Record<string, unknown>;
    components: { schemas: Record<string, unknown> };
  };

  assert.ok(doc.paths['/api/v1/runs/{runId}/teammates']);
  assert.ok(doc.paths['/api/v1/runs/{runId}/teammates/{handle}/steer']);
  assert.ok(doc.paths['/api/v1/runs/{runId}/teammates/{handle}/pause']);
  assert.ok(doc.paths['/api/v1/runs/{runId}/teammates/{handle}/resume']);
  assert.ok(doc.components.schemas.TeammateSnapshot);
  assert.ok(doc.components.schemas.TeammateSteerReceipt);
  assert.ok(doc.components.schemas.TeammateControlReceipt);
});
