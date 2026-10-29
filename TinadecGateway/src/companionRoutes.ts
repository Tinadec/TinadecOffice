import type { AnyElysia } from 'elysia';
import { proxyRaw } from './coreClient.js';
import { externalJsonResponse } from './externalDtoOpenApi.js';

/**
 * Companion domain contract forwarding (spec: add-companion-mode, Task 4). Every route forwards
 * Core's bytes, status, ProblemDetails and headers verbatim; the Gateway keeps no companion
 * state, recomputes nothing and never rewrites a refusal code. `If-Match`, `ETag` and
 * `Idempotency-Key` travel through `forwardHeaders` untouched.
 *
 * Order matters: Elysia resolves matching routes in registration order, so the static
 * `/api/v1/companions/activity` must be registered before `/api/v1/companions/:companionId`,
 * otherwise the literal segment is consumed as a companion id.
 */

type Verb = 'GET' | 'POST' | 'PATCH' | 'DELETE';

interface CompanionRoute {
  method: Verb;
  path: string;
  summary: string;
  /** Response component for the success status; omitted for a bodyless 204. */
  response?: string;
  status?: 200 | 201 | 204;
}

// The complete surface Core actually serves (`AspNetCore/Endpoints/CompanionEndpoints.cs` and
// `CompanionConversationEndpoints.cs`). Keep this list exhaustive and ordered: `companionRoutes.test.ts`
// pins the set so a contract entry cannot silently disappear or turn into a ghost route.
const companionRoutes: CompanionRoute[] = [
  { method: 'GET', path: '/api/v1/companion-settings', summary: 'Read companion settings (main companion and global autonomy pause)', response: 'CompanionSettings' },
  { method: 'PATCH', path: '/api/v1/companion-settings', summary: 'Update companion settings', response: 'CompanionSettings' },
  { method: 'GET', path: '/api/v1/companion-connections', summary: 'List every companion connection binding with its owner name', response: 'CompanionConnectionOverviewList' },
  // Static companion paths first — see the ordering note above.
  { method: 'GET', path: '/api/v1/companions/activity', summary: 'Companion activity feed', response: 'CompanionActivityList' },
  { method: 'GET', path: '/api/v1/companions', summary: 'List companions (optionally including archived)', response: 'CompanionList' },
  { method: 'POST', path: '/api/v1/companions', summary: 'Create a companion', response: 'CompanionDetail' },
  { method: 'GET', path: '/api/v1/companions/:companionId', summary: 'Read a companion profile and autonomy rules', response: 'CompanionDetail' },
  { method: 'PATCH', path: '/api/v1/companions/:companionId', summary: 'Update a companion profile (If-Match revision)', response: 'CompanionDetail' },
  { method: 'POST', path: '/api/v1/companions/:companionId/archive', summary: 'Archive a companion (If-Match revision)', response: 'CompanionDetail' },
  { method: 'POST', path: '/api/v1/companions/:companionId/autonomy/tick', summary: 'Run one bounded autonomy tick for a companion', response: 'CompanionAutonomyTickResult' },
  { method: 'GET', path: '/api/v1/companions/:companionId/memories', summary: 'List or search private memories (q, limit)', response: 'CompanionMemoryList' },
  { method: 'POST', path: '/api/v1/companions/:companionId/memories', summary: 'Write a private memory', response: 'CompanionMemory' },
  { method: 'PATCH', path: '/api/v1/companions/:companionId/memories/:memoryId', summary: 'Edit a private memory (If-Match revision)', response: 'CompanionMemory' },
  { method: 'DELETE', path: '/api/v1/companions/:companionId/memories/:memoryId', summary: 'Delete a private memory (If-Match revision)', status: 204 },
  { method: 'GET', path: '/api/v1/companions/:companionId/skills', summary: 'List companion skill bindings', response: 'CompanionSkillList' },
  { method: 'POST', path: '/api/v1/companions/:companionId/skills', summary: 'Bind a skill to a companion', response: 'CompanionSkill' },
  { method: 'PATCH', path: '/api/v1/companions/:companionId/skills/:bindingId', summary: 'Update a skill binding (If-Match revision)', response: 'CompanionSkill' },
  { method: 'DELETE', path: '/api/v1/companions/:companionId/skills/:bindingId', summary: 'Delete a skill binding (If-Match revision)', status: 204 },
  { method: 'GET', path: '/api/v1/companions/:companionId/connections', summary: 'List companion connection bindings', response: 'CompanionConnectionList' },
  { method: 'POST', path: '/api/v1/companions/:companionId/connections', summary: 'Bind a connection to a companion', response: 'CompanionConnection' },
  { method: 'PATCH', path: '/api/v1/companions/:companionId/connections/:bindingId', summary: 'Update a connection binding (If-Match revision)', response: 'CompanionConnection' },
  { method: 'DELETE', path: '/api/v1/companions/:companionId/connections/:bindingId', summary: 'Delete a connection binding (If-Match revision)', status: 204 },
  { method: 'GET', path: '/api/v1/companions/:companionId/conversations', summary: 'List conversations for a companion', response: 'CompanionConversationList' },
  { method: 'POST', path: '/api/v1/companions/:companionId/conversations', summary: 'Ensure a private chat or create a group', response: 'CompanionConversation' },
  { method: 'GET', path: '/api/v1/companion-conversations/:conversationId', summary: 'Read one companion conversation with members', response: 'CompanionConversation' },
  { method: 'GET', path: '/api/v1/companion-conversations', summary: 'List every conversation across companions (companion_id, kind, origin, limit)', response: 'CompanionConversationList' },
  { method: 'POST', path: '/api/v1/companion-conversations/:conversationId/members', summary: 'Invite a companion (creates a linked group from a private chat)', response: 'CompanionConversation' },
  { method: 'DELETE', path: '/api/v1/companion-conversations/:conversationId/members/:companionId', summary: 'Remove a companion from a conversation', response: 'CompanionConversation' },
  { method: 'GET', path: '/api/v1/companion-conversations/:conversationId/messages', summary: 'List messages after a seq cursor (after_seq, limit)', response: 'CompanionMessageList' },
  { method: 'POST', path: '/api/v1/companion-conversations/:conversationId/messages', summary: 'Post a user message and create the runs it owes (Idempotency-Key)', response: 'CompanionSendMessageResult' },
  { method: 'POST', path: '/api/v1/companion-conversations/:conversationId/pause', summary: 'Pause or resume a conversation', response: 'CompanionConversation' },
  { method: 'GET', path: '/api/v1/companion-runs/:runId', summary: 'Poll one companion run', response: 'CompanionRun' },
];

/** Core owns companion identity, visibility, revisions, budgets and refusals. */
export function registerCompanionRoutes(app: AnyElysia, forwardHeaders: (request: Request) => Record<string, string>) {
  for (const route of companionRoutes) {
    app.route(route.method, route.path, async context => {
      const request = context.request as Request;
      const url = new URL(request.url);
      const body: unknown = context.body;
      return proxyRaw(url.pathname + url.search, {
        method: route.method,
        headers: forwardHeaders(request),
        // GET/DELETE never carry a body; every write forwards the parsed JSON verbatim.
        body: route.method === 'GET' || route.method === 'DELETE' || body === undefined
          ? undefined
          : JSON.stringify(body),
        signal: request.signal,
      });
    }, { detail: {
      summary: route.summary,
      tags: ['Companions'],
      responses: route.response
        ? { [route.status ?? 200]: externalJsonResponse(route.response, route.summary) }
        : { 204: { description: 'Deleted; Core returned no content.' } },
    } as never });
  }
}
