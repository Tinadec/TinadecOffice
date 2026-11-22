import type { AnyElysia } from 'elysia';
import { proxyRaw, proxySse } from './coreClient.js';
import { wrapSseWithKeepalive } from './streaming.js';

const ref = (name: string) => ({ $ref: `#/components/schemas/${name}` });

export const liveExplainerSchemas = {
  LiveDigestSettings: {
    type: 'object',
    properties: {
      mode: { type: 'string', enum: ['off', 'auto', 'always'] },
      route_purpose: { type: 'string' },
      provider_instance_id: { type: 'string', format: 'uuid', nullable: true },
      model: { type: 'string', nullable: true },
      resolved_provider_instance_id: { type: 'string', format: 'uuid', nullable: true },
      resolved_model: { type: 'string', nullable: true },
      resolved_source: {
        type: 'string',
        enum: ['explicit_route', 'auto_fast_model', 'chat_fallback', 'unconfigured'],
      },
      revision: { type: 'integer' },
      updated_at: { type: 'string', format: 'date-time' },
    },
    required: ['mode', 'route_purpose', 'resolved_source', 'revision', 'updated_at'],
  },
  UpdateLiveDigestSettingsRequest: {
    type: 'object',
    properties: {
      mode: { type: 'string', enum: ['off', 'auto', 'always'], nullable: true },
      provider_instance_id: { type: 'string', format: 'uuid', nullable: true },
      model: { type: 'string', nullable: true },
      clear_model_binding: { type: 'boolean', nullable: true },
    },
    additionalProperties: true,
  },
  CompactionPoint: {
    type: 'object',
    properties: {
      seq: { type: 'integer' },
      before_tokens: { type: 'integer' },
      after_tokens: { type: 'integer' },
      occurred_at: { type: 'string', format: 'date-time' },
      summary: { type: 'string' },
    },
    required: ['seq', 'before_tokens', 'after_tokens', 'occurred_at', 'summary'],
  },
  ContextWaterline: {
    type: 'object',
    properties: {
      used_tokens: { type: 'integer' },
      max_tokens: { type: 'integer' },
      usage_percent: { type: 'number' },
      compaction_count: { type: 'integer' },
      compaction_points: { type: 'array', items: ref('CompactionPoint') },
    },
    required: ['used_tokens', 'max_tokens', 'usage_percent', 'compaction_count', 'compaction_points'],
  },
  GovernanceSignal: {
    type: 'object',
    properties: {
      id: { type: 'string' },
      code: {
        type: 'string',
        enum: [
          'APPROVAL_REJECTED',
          'FS_STALE_VERSION',
          'SAME_ARGS_RETRY',
          'TIMEOUT',
          'WORKTREE_GUARD',
          'LOOP_DETECTED',
          'CONTEXT_PRESSURE',
        ],
      },
      severity: { type: 'string', enum: ['info', 'warning', 'critical'] },
      summary: { type: 'string' },
      subject_id: { type: 'string', nullable: true },
      occurred_at: { type: 'string', format: 'date-time' },
    },
    required: ['id', 'code', 'severity', 'summary', 'occurred_at'],
  },
  TeammateThought: {
    type: 'object',
    properties: {
      handle: { type: 'string' },
      display_name: { type: 'string' },
      status: { type: 'string' },
      current_task: { type: 'string', nullable: true },
      thought: { type: 'string' },
      updated_at: { type: 'string', format: 'date-time' },
    },
    required: ['handle', 'display_name', 'status', 'thought', 'updated_at'],
  },
  ChapterToolCall: {
    type: 'object',
    properties: {
      tool_call_id: { type: 'string' },
      tool_name: { type: 'string' },
      status: { type: 'string', enum: ['running', 'succeeded', 'failed'] },
      compact_summary: { type: 'string' },
    },
    required: ['tool_call_id', 'tool_name', 'status', 'compact_summary'],
  },
  TouchedFileStat: {
    type: 'object',
    properties: {
      path: { type: 'string' },
      added_lines: { type: 'integer' },
      removed_lines: { type: 'integer' },
    },
    required: ['path', 'added_lines', 'removed_lines'],
  },
  LiveDigestChapter: {
    type: 'object',
    properties: {
      id: { type: 'string' },
      index: { type: 'integer' },
      phase: { type: 'string', enum: ['hypothesis', 'verification', 'conclusion'] },
      title: { type: 'string' },
      narrative: { type: 'string' },
      source: { type: 'string', enum: ['l0_structural', 'l1_narrator', 'l2_scout'] },
      raw_thought_excerpt: { type: 'string', nullable: true },
      scout_note: { type: 'string', nullable: true },
      tool_calls: { type: 'array', items: ref('ChapterToolCall') },
      touched_files: { type: 'array', items: ref('TouchedFileStat') },
      started_at: { type: 'string', format: 'date-time' },
      updated_at: { type: 'string', format: 'date-time' },
    },
    required: [
      'id',
      'index',
      'phase',
      'title',
      'narrative',
      'source',
      'tool_calls',
      'touched_files',
      'started_at',
      'updated_at',
    ],
  },
  SentinelAlert: {
    type: 'object',
    properties: {
      id: { type: 'string' },
      code: { type: 'string' },
      severity: { type: 'string', enum: ['warning', 'critical'] },
      title: { type: 'string' },
      message: { type: 'string' },
      suggested_steer_prompt: { type: 'string' },
      teammate_handle: { type: 'string', nullable: true },
      created_at: { type: 'string', format: 'date-time' },
    },
    required: ['id', 'code', 'severity', 'title', 'message', 'suggested_steer_prompt', 'created_at'],
  },
  LiveDigestSnapshot: {
    type: 'object',
    properties: {
      run_id: { type: 'string', format: 'uuid' },
      session_id: { type: 'string', format: 'uuid' },
      status: { type: 'string' },
      mode: { type: 'string', enum: ['off', 'auto', 'always'] },
      explainer_active: { type: 'boolean' },
      explainer_model: { type: 'string', nullable: true },
      seq: { type: 'integer' },
      headline: { type: 'string' },
      current_phase: { type: 'string', enum: ['hypothesis', 'verification', 'conclusion'] },
      context_waterline: ref('ContextWaterline'),
      governance_signals: { type: 'array', items: ref('GovernanceSignal') },
      teammate_thoughts: { type: 'array', items: ref('TeammateThought') },
      chapters: { type: 'array', items: ref('LiveDigestChapter') },
      touched_files: { type: 'array', items: ref('TouchedFileStat') },
      sentinel_alerts: { type: 'array', items: ref('SentinelAlert') },
      updated_at: { type: 'string', format: 'date-time' },
    },
    required: [
      'run_id',
      'session_id',
      'status',
      'mode',
      'explainer_active',
      'seq',
      'headline',
      'current_phase',
      'context_waterline',
      'governance_signals',
      'teammate_thoughts',
      'chapters',
      'touched_files',
      'sentinel_alerts',
      'updated_at',
    ],
  },
};

function keepStreamOpen(server: unknown, request: Request) {
  try {
    (server as { timeout?(req: Request, sec: number): void } | null)?.timeout?.(request, 0);
  } catch {
    // app.handle() in unit tests runs without a live Bun server socket.
  }
}

/**
 * Task #51: Governance-layer read-only Live Digest settings, snapshot replay, and unbuffered SSE stream proxy.
 */
export function registerLiveExplainerRoutes(
  app: AnyElysia,
  forwardHeaders: (request: Request) => Record<string, string>,
) {
  app.route(
    'GET',
    '/api/v1/live-digest/settings',
    async (context) => {
      const request = context.request as Request;
      const url = new URL(request.url);
      return proxyRaw(url.pathname + url.search, {
        method: 'GET',
        headers: forwardHeaders(request),
        signal: request.signal,
      });
    },
    {
      detail: {
        summary: 'GET /api/v1/live-digest/settings',
        tags: ['ModelCenter'],
        responses: {
          200: {
            description: 'Live Digest 3-tier mode and live_explainer route slot resolution',
            content: { 'application/json': { schema: ref('LiveDigestSettings') } },
          },
        },
      } as never,
    },
  );

  app.route(
    'PUT',
    '/api/v1/live-digest/settings',
    async (context) => {
      const request = context.request as Request;
      const url = new URL(request.url);
      const rawBody =
        context.body === undefined
          ? '{}'
          : typeof context.body === 'string'
            ? context.body
            : JSON.stringify(context.body);
      return proxyRaw(url.pathname + url.search, {
        method: 'PUT',
        headers: forwardHeaders(request),
        body: rawBody,
        signal: request.signal,
      });
    },
    {
      detail: {
        summary: 'PUT /api/v1/live-digest/settings',
        tags: ['ModelCenter'],
        requestBody: {
          required: true,
          content: { 'application/json': { schema: ref('UpdateLiveDigestSettingsRequest') } },
        },
        responses: {
          200: {
            description: 'Updated Live Digest settings',
            content: { 'application/json': { schema: ref('LiveDigestSettings') } },
          },
        },
      } as never,
    },
  );

  // Register the `/stream` route before `/live-digest` so path matching stays unambiguous.
  app.route(
    'GET',
    '/api/v1/runs/:runId/live-digest/stream',
    async (context) => {
      const request = context.request as Request;
      keepStreamOpen(context.server, request);
      const url = new URL(request.url);
      const headers = forwardHeaders(request);
      const cursor =
        url.searchParams.get('after_seq') ??
        url.searchParams.get('cursor') ??
        request.headers.get('last-event-id') ??
        request.headers.get('Last-Event-ID');

      const search = new URLSearchParams(url.searchParams);
      if (cursor && !search.has('after_seq')) {
        search.set('after_seq', cursor);
      }
      const suffix = search.toString() ? `?${search.toString()}` : '';

      const response = await proxySse(`${url.pathname}${suffix}`, {
        headers: {
          ...headers,
          ...(cursor ? { 'last-event-id': cursor } : {}),
        },
        signal: request.signal,
      });

      if (response.status >= 400) {
        return response;
      }

      const set = context.set as { headers: Record<string, string | number>; status?: number | string };
      set.status = response.status;
      set.headers['content-type'] = response.headers.get('content-type') ?? 'text/event-stream';
      set.headers['cache-control'] = 'no-cache, no-transform';
      set.headers['connection'] = 'keep-alive';
      set.headers['x-accel-buffering'] = 'no';

      const sseHeaders = new Headers(response.headers);
      sseHeaders.set('content-type', response.headers.get('content-type') ?? 'text/event-stream');
      sseHeaders.set('cache-control', 'no-cache, no-transform');
      sseHeaders.set('connection', 'keep-alive');
      sseHeaders.set('x-accel-buffering', 'no');

      return new Response(wrapSseWithKeepalive(response.body, 15_000, request.signal), {
        status: response.status,
        headers: sseHeaders,
      });
    },
    {
      detail: {
        summary: 'GET /api/v1/runs/{runId}/live-digest/stream',
        tags: ['Runs'],
        description:
          'Unbuffered SSE stream for Live Digest (snapshot, l0_delta, l1_narration, done) with Last-Event-ID / ?after_seq resume.',
        responses: {
          200: {
            description: 'Live Digest SSE event stream',
            content: { 'text/event-stream': { schema: { type: 'string' } } },
          },
        },
      } as never,
    },
  );

  app.route(
    'GET',
    '/api/v1/runs/:runId/live-digest',
    async (context) => {
      const request = context.request as Request;
      const url = new URL(request.url);
      return proxyRaw(url.pathname + url.search, {
        method: 'GET',
        headers: forwardHeaders(request),
        signal: request.signal,
      });
    },
    {
      detail: {
        summary: 'GET /api/v1/runs/{runId}/live-digest',
        tags: ['Runs'],
        responses: {
          200: {
            description: 'Durable Live Digest snapshot for live view or post-run replay',
            content: { 'application/json': { schema: ref('LiveDigestSnapshot') } },
          },
        },
      } as never,
    },
  );
}
