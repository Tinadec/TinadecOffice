import type { AnyElysia } from 'elysia';
import { proxyRaw } from './coreClient.js';
import contract from './contracts/organization.openapi.json';

export const organizationSchemas = contract.components.schemas;

/**
 * A session's organization (members, rooms, reports), its graph (topology) and the delegated
 * approval gates its members decide (GET /api/v1/approvals/{approvalId}/gates), and the
 * workspace's environment registry (GET/POST /api/v1/environments, PATCH …/{id}). Core owns every
 * visibility and write rule, so these routes forward verbatim — status codes and problem details
 * included — and the documentation is generated from Core's snapshot, not a second validator.
 */
export function registerOrganizationRoutes(app: AnyElysia, forwardHeaders: (request: Request) => Record<string, string>) {
  for (const [path, operations] of Object.entries(contract.paths)) {
    if (!/^\/api\/v1\/(sessions\/\{sessionId\}\/(organization|topology|evidence)(\/|$)|approvals\/\{approvalId\}\/gates$|environments(\/|$))/.test(path)) {
      throw new Error(`Unexpected organization contract path: ${path}`);
    }
    for (const [method, detail] of Object.entries(operations)) {
      if (!['get', 'post', 'patch'].includes(method)) continue;
      const verb = method.toUpperCase() as 'GET' | 'POST' | 'PATCH';
      app.route(verb, path.replace(/\{([^}]+)\}/g, ':$1'), async context => {
        const request = context.request as Request;
        const body: unknown = context.body;
        const url = new URL(request.url);
        return proxyRaw(url.pathname + url.search, {
          method: verb,
          headers: forwardHeaders(request),
          body: verb === 'GET' || body === undefined ? undefined : JSON.stringify(body),
        });
      }, { detail: detail as never });
    }
  }
}
