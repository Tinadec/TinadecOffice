import type { AnyElysia } from 'elysia';
import { proxyRaw } from './coreClient.js';
import contract from './contracts/tina-chat.openapi.json';

export const tinaChatSchemas = contract.components.schemas;

/** All validation and visibility decisions belong to Core. Documentation is generated, not a second validator. */
export function registerTinaChatRoutes(app: AnyElysia, forwardHeaders: (request: Request) => Record<string, string>) {
  for (const [path, operations] of Object.entries(contract.paths)) {
    if (!path.startsWith('/api/v1/tina-chat/')) throw new Error(`Unexpected TinaChat contract path: ${path}`);
    for (const [method, detail] of Object.entries(operations)) {
      if (!['get', 'post', 'put', 'patch'].includes(method)) continue;
      const verb = method.toUpperCase() as 'GET' | 'POST' | 'PUT' | 'PATCH';
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
