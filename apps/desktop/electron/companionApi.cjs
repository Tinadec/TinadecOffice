const { randomUUID } = require('node:crypto');

/**
 * Main-process companion proxy (spec: add-companion-mode, Task 5).
 *
 * The companion window is deliberately not a trusted host: `trustedHostRequests` withholds the
 * host-control credential from it and `main.cjs` gives it no business IPC. Its chat work therefore
 * goes through this single channel, which pins the origin, injects the credential in the main
 * process only, and refuses every path outside the companion read/speak/invite/pause whitelist.
 *
 * The function never throws across an IPC boundary except for a sender that is not the companion
 * window; every other failure is a `{status, body}` value, and no credential or upstream error text
 * is ever included in it.
 */

const GUID = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';

const routes = [
  { method: 'GET', pattern: `^/api/v1/companions$` },
  { method: 'GET', pattern: `^/api/v1/companions/activity$` },
  { method: 'GET', pattern: `^/api/v1/companions/${GUID}$` },
  { method: 'GET', pattern: `^/api/v1/companions/${GUID}/conversations$` },
  { method: 'POST', pattern: `^/api/v1/companions/${GUID}/conversations$` },
  { method: 'POST', pattern: `^/api/v1/companions/${GUID}/autonomy/tick$` },
  { method: 'GET', pattern: `^/api/v1/companion-conversations/${GUID}$` },
  { method: 'POST', pattern: `^/api/v1/companion-conversations/${GUID}/members$` },
  { method: 'GET', pattern: `^/api/v1/companion-conversations/${GUID}/messages$` },
  { method: 'POST', pattern: `^/api/v1/companion-conversations/${GUID}/messages$` },
  { method: 'POST', pattern: `^/api/v1/companion-conversations/${GUID}/pause$` },
  { method: 'GET', pattern: `^/api/v1/companion-runs/${GUID}$` },
  { method: 'GET', pattern: `^/api/v1/companion-settings$` },
  { method: 'PATCH', pattern: `^/api/v1/companion-settings$` },
].map(route => ({ method: route.method, pattern: new RegExp(route.pattern, 'i') }));

const allowedQueryKeys = new Set(['limit', 'after_seq', 'include_archived', 'q']);
const allowedMethods = new Set(['GET', 'POST', 'PATCH']);
const maxQueryLength = 1024;

function isAllowedRoute(method, pathname) {
  return routes.some(route => route.method === method && route.pattern.test(pathname));
}

function parsePath(value) {
  if (typeof value !== 'string' || !value.startsWith('/api/v1/')) return null;
  if (value.includes('://') || value.includes('#') || value.length > 2048) return null;
  if (/[\s\r\n]/.test(value)) return null;
  const marker = value.indexOf('?');
  const pathname = marker < 0 ? value : value.slice(0, marker);
  const search = marker < 0 ? '' : value.slice(marker + 1);
  if (search.length > maxQueryLength) return null;
  let decoded;
  try { decoded = decodeURIComponent(pathname); } catch { return null; }
  if (decoded.includes('..')) return null;
  if (search) {
    for (const key of new URLSearchParams(search).keys()) {
      if (!allowedQueryKeys.has(key)) return null;
    }
  }
  return { pathname, search };
}

function serializeBody(body, maxBodyBytes) {
  if (body === undefined) return { ok: true, text: undefined };
  if (body === null || typeof body !== 'object' || Array.isArray(body)) return { ok: false };
  let text;
  try { text = JSON.stringify(body); } catch { return { ok: false }; }
  if (typeof text !== 'string' || Buffer.byteLength(text, 'utf8') > maxBodyBytes) return { ok: false };
  return { ok: true, text };
}

function invalid(message) {
  return { status: 400, body: { code: 'invalid_request', message } };
}

function createCompanionApiBridge({
  baseUrl,
  token,
  isCompanionWindowSender,
  fetchImpl = globalThis.fetch,
  timeoutMs = 15000,
  maxBodyBytes = 64 * 1024,
} = {}) {
  if (typeof isCompanionWindowSender !== 'function') throw new Error('A companion window sender check is required.');
  if (typeof fetchImpl !== 'function') throw new Error('A fetch implementation is required.');
  const origin = typeof baseUrl === 'string' ? baseUrl.replace(/\/+$/, '') : '';

  return async function bridge(event, payload) {
    if (!isCompanionWindowSender(event?.sender)) {
      throw new Error('Companion API IPC is only available to the companion window.');
    }
    if (!origin || typeof token !== 'string' || token.length === 0) {
      return { status: 503, body: { code: 'host_not_ready' } };
    }
    if (!payload || typeof payload !== 'object' || Array.isArray(payload)) {
      return invalid('A companion request payload is required.');
    }
    const method = payload.method;
    if (!allowedMethods.has(method)) return invalid('Unsupported companion request method.');
    const path = parsePath(payload.path);
    if (!path || !isAllowedRoute(method, path.pathname)) {
      return invalid('Path is not allowed for the companion window.');
    }
    if (method === 'GET' && payload.body !== undefined) {
      return invalid('GET requests cannot carry a body.');
    }
    const body = serializeBody(payload.body, maxBodyBytes);
    if (!body.ok) return invalid('Request body must be a JSON object within the size limit.');
    if (body.text !== undefined && method === 'GET') return invalid('GET requests cannot carry a body.');

    const target = `${origin}${path.pathname}${path.search ? `?${path.search}` : ''}`;
    let response;
    try {
      response = await fetchImpl(target, {
        method,
        headers: {
          accept: 'application/json',
          'content-type': 'application/json',
          'x-tinadec-host-control': token,
          'x-request-id': randomUUID(),
        },
        ...(body.text === undefined ? {} : { body: body.text }),
        signal: AbortSignal.timeout(timeoutMs),
        redirect: 'error',
      });
    } catch {
      return { status: 502, body: { code: 'gateway_unreachable' } };
    }

    let text;
    try {
      text = await response.text();
    } catch {
      return { status: 502, body: { code: 'gateway_unreachable' } };
    }
    if (typeof text !== 'string' || text.length === 0) {
      return { status: response.status, body: null };
    }
    try {
      return { status: response.status, body: JSON.parse(text) };
    } catch {
      return { status: response.status, body: { code: 'invalid_upstream_response' } };
    }
  };
}

module.exports = { createCompanionApiBridge };
