import type { AnyElysia } from 'elysia';
import { proxyRaw } from './coreClient.js';

const ref = (name: string) => ({ $ref: `#/components/schemas/${name}` });

export const contextMemorySchemas = {
  ContextMemoryPolicy: {
    type: 'object',
    properties: {
      strategy: { type: 'string', enum: ['direct', 'managed', 'deep_curator'] },
      recall_fidelity: { type: 'string', enum: ['summary_only', 'balanced', 'verbatim_first'] },
      compaction_trigger_ratio: { type: 'number' },
      recent_message_limit: { type: 'integer' },
      default_token_budget: { type: 'integer' },
      allow_cross_session_recall: { type: 'boolean' },
      micro_compact_stale_tool_outputs: { type: 'boolean' },
      offload_large_tool_payload_bytes: { type: 'integer' },
      evolution_mode: { type: 'string', enum: ['auto_evolve', 'candidate_review', 'frozen'] },
      candidate_only: { type: 'boolean' },
      retrieval_limit: { type: 'integer' },
      core_markdown_max_tokens: { type: 'integer' },
      project_markdown_projection: { type: 'boolean' },
      project_memory_path: { type: 'string' },
      companion_can_write_project_memory: { type: 'boolean' },
      allowed_scopes: { type: 'array', items: { type: 'string' } },
      allowed_kinds: { type: 'array', items: { type: 'string' } },
      always_on_primitives: { type: 'array', items: { type: 'string' } },
      source_path: { type: 'string', nullable: true },
      content_hash: { type: 'string', nullable: true },
      version: { type: 'integer' },
    },
    required: [
      'strategy', 'recall_fidelity', 'compaction_trigger_ratio', 'recent_message_limit',
      'default_token_budget', 'allow_cross_session_recall', 'micro_compact_stale_tool_outputs',
      'offload_large_tool_payload_bytes', 'evolution_mode', 'candidate_only', 'retrieval_limit',
      'core_markdown_max_tokens', 'project_markdown_projection', 'project_memory_path',
      'companion_can_write_project_memory', 'allowed_scopes', 'allowed_kinds',
      'always_on_primitives', 'version',
    ],
  },
  UpdateContextMemoryPolicyRequest: {
    type: 'object',
    properties: {
      strategy: { type: 'string', nullable: true },
      recall_fidelity: { type: 'string', nullable: true },
      compaction_trigger_ratio: { type: 'number', nullable: true },
      recent_message_limit: { type: 'integer', nullable: true },
      default_token_budget: { type: 'integer', nullable: true },
      allow_cross_session_recall: { type: 'boolean', nullable: true },
      offload_large_tool_payload_bytes: { type: 'integer', nullable: true },
      evolution_mode: { type: 'string', nullable: true },
      candidate_only: { type: 'boolean', nullable: true },
      retrieval_limit: { type: 'integer', nullable: true },
      core_markdown_max_tokens: { type: 'integer', nullable: true },
      project_markdown_projection: { type: 'boolean', nullable: true },
      project_memory_path: { type: 'string', nullable: true },
      companion_can_write_project_memory: { type: 'boolean', nullable: true },
      expected_content_hash: { type: 'string', nullable: true },
      expected_hash: { type: 'string', nullable: true },
    },
    additionalProperties: true,
  },
  MemoryVersionLedger: {
    type: 'object',
    properties: {
      id: { type: 'string', format: 'uuid' },
      version: { type: 'integer' },
      action: { type: 'string' },
      content: { type: 'string' },
      previous_content: { type: 'string', nullable: true },
      source_session_id: { type: 'string', format: 'uuid', nullable: true },
      source_message_seq: { type: 'integer', nullable: true },
      source_run_id: { type: 'string', format: 'uuid', nullable: true },
      reason: { type: 'string', nullable: true },
      created_at: { type: 'string', format: 'date-time' },
    },
    required: ['id', 'version', 'action', 'content', 'created_at'],
  },
  MemoryItemLedger: {
    type: 'object',
    properties: {
      id: { type: 'string', format: 'uuid' },
      scope: { type: 'string' },
      kind: { type: 'string' },
      section: { type: 'string', nullable: true },
      status: { type: 'string' },
      current_version: { type: 'integer' },
      content: { type: 'string' },
      previous_content: { type: 'string', nullable: true },
      action: { type: 'string' },
      project_id: { type: 'string', format: 'uuid', nullable: true },
      companion_id: { type: 'string', format: 'uuid', nullable: true },
      source_session_id: { type: 'string', format: 'uuid', nullable: true },
      source_message_seq: { type: 'integer', nullable: true },
      source_run_id: { type: 'string', format: 'uuid', nullable: true },
      superseded_by_id: { type: 'string', format: 'uuid', nullable: true },
      applicability: { type: 'string', nullable: true },
      expiry_condition: { type: 'string', nullable: true },
      revocation_reason: { type: 'string', nullable: true },
      estimated_tokens: { type: 'integer' },
      in_hot_markdown: { type: 'boolean' },
      created_at: { type: 'string', format: 'date-time' },
      updated_at: { type: 'string', format: 'date-time' },
      revoked_at: { type: 'string', format: 'date-time', nullable: true },
      versions: { type: 'array', items: ref('MemoryVersionLedger') },
    },
    required: [
      'id', 'scope', 'kind', 'status', 'current_version', 'content',
      'action', 'estimated_tokens', 'in_hot_markdown', 'created_at', 'updated_at', 'versions',
    ],
  },
  MemoryItemListResult: {
    type: 'object',
    properties: {
      items: { type: 'array', items: ref('MemoryItemLedger') },
      total_count: { type: 'integer' },
      active_count: { type: 'integer' },
      hot_count: { type: 'integer' },
      cold_count: { type: 'integer' },
    },
    required: ['items', 'total_count', 'active_count', 'hot_count', 'cold_count'],
  },
  UpsertMemoryItemRequest: {
    type: 'object',
    properties: {
      scope: { type: 'string' },
      kind: { type: 'string', nullable: true },
      section: { type: 'string', nullable: true },
      content: { type: 'string' },
      project_id: { type: 'string', format: 'uuid', nullable: true },
      workspace_root: { type: 'string', nullable: true },
      companion_id: { type: 'string', format: 'uuid', nullable: true },
      source_session_id: { type: 'string', format: 'uuid', nullable: true },
      source_message_seq: { type: 'integer', nullable: true },
      source_run_id: { type: 'string', format: 'uuid', nullable: true },
      supersede_item_id: { type: 'string', format: 'uuid', nullable: true },
      supersedes_id: { type: 'string', format: 'uuid', nullable: true },
      applicability: { type: 'string', nullable: true },
      expiry_condition: { type: 'string', nullable: true },
      reason: { type: 'string', nullable: true },
      is_companion_writer: { type: 'boolean' },
      respect_evolution_mode: { type: 'boolean' },
    },
    required: ['scope', 'content'],
    additionalProperties: true,
  },
  RevertMemoryItemRequest: {
    type: 'object',
    properties: {
      target_version: { type: 'integer', nullable: true },
      reason: { type: 'string', nullable: true },
    },
    additionalProperties: true,
  },
  MemoryDocumentSection: {
    type: 'object',
    properties: {
      heading: { type: 'string' },
      key: { type: 'string' },
      item_count: { type: 'integer' },
      estimated_tokens: { type: 'integer' },
    },
    required: ['heading', 'key', 'item_count', 'estimated_tokens'],
  },
  MemoryDocument: {
    type: 'object',
    properties: {
      scope: { type: 'string' },
      project_id: { type: 'string', format: 'uuid', nullable: true },
      companion_id: { type: 'string', format: 'uuid', nullable: true },
      file_path: { type: 'string' },
      markdown: { type: 'string' },
      estimated_tokens: { type: 'integer' },
      core_markdown_max_tokens: { type: 'integer' },
      active_hot_count: { type: 'integer' },
      active_item_count: { type: 'integer' },
      cold_archive_count: { type: 'integer' },
      content_hash: { type: 'string' },
      updated_at: { type: 'string', format: 'date-time' },
      sections: { type: 'array', items: ref('MemoryDocumentSection') },
      items: { type: 'array', items: ref('MemoryItemLedger') },
    },
    required: [
      'scope', 'file_path', 'markdown', 'estimated_tokens', 'core_markdown_max_tokens',
      'active_hot_count', 'active_item_count', 'cold_archive_count', 'content_hash',
      'updated_at', 'sections', 'items',
    ],
  },
  UpdateMemoryDocumentRequest: {
    type: 'object',
    properties: {
      markdown: { type: 'string' },
      project_id: { type: 'string', format: 'uuid', nullable: true },
      workspace_root: { type: 'string', nullable: true },
      companion_id: { type: 'string', format: 'uuid', nullable: true },
      expected_content_hash: { type: 'string', nullable: true },
      expected_hash: { type: 'string', nullable: true },
      source_session_id: { type: 'string', format: 'uuid', nullable: true },
      source_message_seq: { type: 'integer', nullable: true },
      reason: { type: 'string', nullable: true },
      is_companion_writer: { type: 'boolean' },
    },
    required: ['markdown'],
    additionalProperties: true,
  },
  ConversationRecallRequest: {
    type: 'object',
    properties: {
      query: { type: 'string' },
      cross_session: { type: 'boolean', nullable: true },
      limit: { type: 'integer', nullable: true },
      fidelity: { type: 'string', nullable: true },
    },
    required: ['query'],
    additionalProperties: true,
  },
  RecallVerbatimSnippet: {
    type: 'object',
    properties: {
      session_id: { type: 'string', format: 'uuid' },
      session_title: { type: 'string', nullable: true },
      message_seq: { type: 'integer' },
      role: { type: 'string' },
      created_at: { type: 'string', format: 'date-time' },
      excerpt: { type: 'string' },
      score: { type: 'number' },
    },
    required: ['session_id', 'message_seq', 'role', 'created_at', 'excerpt', 'score'],
  },
  ConversationRecallResult: {
    type: 'object',
    properties: {
      session_id: { type: 'string', format: 'uuid' },
      query: { type: 'string' },
      fidelity: { type: 'string' },
      cross_session_allowed: { type: 'boolean' },
      cross_session_used: { type: 'boolean' },
      summary: { type: 'string' },
      verbatim_snippets: { type: 'array', items: ref('RecallVerbatimSnippet') },
      searched_sessions: { type: 'integer' },
      total_matches: { type: 'integer' },
      note: { type: 'string', nullable: true },
    },
    required: [
      'session_id', 'query', 'fidelity', 'cross_session_allowed', 'cross_session_used',
      'summary', 'verbatim_snippets', 'searched_sessions', 'total_matches',
    ],
  },
};

type RouteSpec = {
  method: 'GET' | 'PUT' | 'POST' | 'DELETE';
  path: string;
  response: string;
  statusCode?: number;
  bodySchema?: string;
  bodyRequired?: boolean;
};

/** Phase 11 Context & Memory HTTP proxy (policy, dual-layer Markdown documents, item ledger & recall). */
export function registerContextMemoryRoutes(app: AnyElysia, forwardHeaders: (request: Request) => Record<string, string>) {
  const routes: RouteSpec[] = [
    { method: 'GET', path: '/api/v1/context-memory/policy', response: 'ContextMemoryPolicy' },
    { method: 'PUT', path: '/api/v1/context-memory/policy', response: 'ContextMemoryPolicy', bodySchema: 'UpdateContextMemoryPolicyRequest', bodyRequired: true },
    { method: 'GET', path: '/api/v1/memories/documents/:scope', response: 'MemoryDocument' },
    { method: 'PUT', path: '/api/v1/memories/documents/:scope', response: 'MemoryDocument', bodySchema: 'UpdateMemoryDocumentRequest', bodyRequired: true },
    { method: 'GET', path: '/api/v1/memories/items', response: 'MemoryItemListResult' },
    { method: 'POST', path: '/api/v1/memories/items', response: 'MemoryItemLedger', statusCode: 201, bodySchema: 'UpsertMemoryItemRequest', bodyRequired: true },
    { method: 'DELETE', path: '/api/v1/memories/items/:id', response: 'MemoryItemLedger' },
    { method: 'POST', path: '/api/v1/memories/items/:id/revert', response: 'MemoryItemLedger', bodySchema: 'RevertMemoryItemRequest', bodyRequired: false },
    { method: 'POST', path: '/api/v1/sessions/:sessionId/recall', response: 'ConversationRecallResult', bodySchema: 'ConversationRecallRequest', bodyRequired: true },
  ];

  for (const spec of routes) {
    app.route(spec.method, spec.path, async context => {
      const request = context.request as Request;
      const url = new URL(request.url);
      return proxyRaw(url.pathname + url.search, {
        method: spec.method,
        headers: forwardHeaders(request),
        body: spec.method === 'GET' || context.body === undefined ? undefined : JSON.stringify(context.body),
        signal: request.signal,
      });
    }, {
      detail: {
        summary: `${spec.method} ${spec.path}`,
        tags: ['ContextMemory'],
        ...(spec.bodySchema ? { requestBody: { required: spec.bodyRequired ?? true, content: { 'application/json': { schema: ref(spec.bodySchema) } } } } : {}),
        responses: {
          [spec.statusCode ?? 200]: {
            description: 'Core-owned Context & Memory response',
            content: { 'application/json': { schema: ref(spec.response) } },
          },
        },
      } as never,
    });
  }
}
