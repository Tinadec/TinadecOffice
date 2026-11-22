import type { AnyElysia } from 'elysia';
import { proxyRaw } from './coreClient.js';

const ref = (name: string) => ({ $ref: `#/components/schemas/${name}` });

export const teammateControlSchemas = {
  TeammateSnapshot: {
    type: 'object',
    properties: {
      handle: { type: 'string' },
      agent_slug: { type: 'string' },
      mode: { type: 'string', enum: ['teammate', 'ephemeral'] },
      status: {
        type: 'string',
        enum: ['working', 'waiting', 'paused', 'awaiting_approval', 'terminated'],
      },
      paused: { type: 'boolean' },
      awaiting_approval: { type: 'boolean' },
      current_task_id: { type: 'string', format: 'uuid', nullable: true },
      current_task_title: { type: 'string', nullable: true },
      claimed_step_id: { type: 'string', nullable: true },
      latest_summary: { type: 'string', nullable: true },
      runtime_badge: { type: 'string', nullable: true },
      worktree_path: { type: 'string', nullable: true },
      branch_name: { type: 'string', nullable: true },
      write_scope: { type: 'array', items: { type: 'string' } },
      relay_depth: { type: 'integer' },
      pending_steer_count: { type: 'integer' },
      updated_at: { type: 'string', format: 'date-time' },
    },
    required: [
      'handle',
      'agent_slug',
      'mode',
      'status',
      'paused',
      'awaiting_approval',
      'write_scope',
      'relay_depth',
      'pending_steer_count',
      'updated_at',
    ],
  },
  TeammateListResponse: {
    type: 'object',
    properties: {
      run_id: { type: 'string', format: 'uuid' },
      session_id: { type: 'string', format: 'uuid' },
      run_status: { type: 'string' },
      teammates: { type: 'array', items: ref('TeammateSnapshot') },
    },
    required: ['run_id', 'session_id', 'run_status', 'teammates'],
  },
  TeammateSteerRequest: {
    type: 'object',
    properties: {
      message: { type: 'string' },
      urgent: { type: 'boolean', nullable: true },
      client_control_id: { type: 'string', nullable: true },
    },
    required: ['message'],
    additionalProperties: false,
  },
  TeammateSteerReceipt: {
    type: 'object',
    properties: {
      accepted: { type: 'boolean' },
      run_id: { type: 'string', format: 'uuid' },
      handle: { type: 'string' },
      status: { type: 'string' },
      paused: { type: 'boolean' },
      urgent: { type: 'boolean' },
      woken: { type: 'boolean' },
      delivery_mode: {
        type: 'string',
        enum: ['injected_next_turn', 'woken', 'queued_while_paused'],
      },
      board_seq: { type: 'integer' },
      pending_steer_count: { type: 'integer' },
      task_id: { type: 'string', format: 'uuid', nullable: true },
      delivered_at: { type: 'string', format: 'date-time' },
    },
    required: [
      'accepted',
      'run_id',
      'handle',
      'status',
      'paused',
      'urgent',
      'woken',
      'delivery_mode',
      'board_seq',
      'pending_steer_count',
      'delivered_at',
    ],
  },
  TeammatePauseRequest: {
    type: 'object',
    properties: {
      reason: { type: 'string', nullable: true },
    },
    additionalProperties: false,
  },
  TeammateControlReceipt: {
    type: 'object',
    properties: {
      accepted: { type: 'boolean' },
      run_id: { type: 'string', format: 'uuid' },
      handle: { type: 'string' },
      action: { type: 'string', enum: ['pause', 'resume'] },
      status: { type: 'string' },
      paused: { type: 'boolean' },
      current_task_id: { type: 'string', format: 'uuid', nullable: true },
      updated_at: { type: 'string', format: 'date-time' },
    },
    required: ['accepted', 'run_id', 'handle', 'action', 'status', 'paused', 'updated_at'],
  },
};

type RouteSpec = {
  method: 'GET' | 'POST';
  path: string;
  response: string;
  bodySchema?: string;
  bodyRequired?: boolean;
};

/**
 * Task #50: Direct human-to-teammate steering, per-teammate pause/resume, and runtime teammate list proxy.
 * Preserves request/tenant/storage scope headers and Core ProblemDetails status codes verbatim.
 */
export function registerTeammateControlRoutes(
  app: AnyElysia,
  forwardHeaders: (request: Request) => Record<string, string>,
) {
  const routes: RouteSpec[] = [
    {
      method: 'GET',
      path: '/api/v1/runs/:runId/teammates',
      response: 'TeammateListResponse',
    },
    {
      method: 'POST',
      path: '/api/v1/runs/:runId/teammates/:handle/steer',
      response: 'TeammateSteerReceipt',
      bodySchema: 'TeammateSteerRequest',
      bodyRequired: true,
    },
    {
      method: 'POST',
      path: '/api/v1/runs/:runId/teammates/:handle/pause',
      response: 'TeammateControlReceipt',
      bodySchema: 'TeammatePauseRequest',
      bodyRequired: false,
    },
    {
      method: 'POST',
      path: '/api/v1/runs/:runId/teammates/:handle/resume',
      response: 'TeammateControlReceipt',
    },
  ];

  for (const spec of routes) {
    app.route(
      spec.method,
      spec.path,
      async (context) => {
        const request = context.request as Request;
        const url = new URL(request.url);
        const rawBody =
          spec.method === 'GET' || context.body === undefined
            ? undefined
            : typeof context.body === 'string'
              ? context.body
              : JSON.stringify(context.body);

        return proxyRaw(url.pathname + url.search, {
          method: spec.method,
          headers: forwardHeaders(request),
          body: rawBody,
          signal: request.signal,
        });
      },
      {
        detail: {
          summary: `${spec.method} ${spec.path}`,
          tags: ['Runs'],
          ...(spec.bodySchema
            ? {
                requestBody: {
                  required: spec.bodyRequired ?? false,
                  content: { 'application/json': { schema: ref(spec.bodySchema) } },
                },
              }
            : {}),
          responses: {
            200: {
              description: 'Core-owned teammate control response',
              content: { 'application/json': { schema: ref(spec.response) } },
            },
          },
        } as never,
      },
    );
  }
}
