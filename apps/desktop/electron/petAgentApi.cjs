const { randomUUID } = require('node:crypto');

/**
 * Main-process proxy for a session partner window. Like the companion window, a session pet
 * window is not a trusted host: it holds no host-control credential and gets no business IPC.
 * Its approval-bubble work goes through this one channel, which pins the origin, injects the
 * credential in the main process only, and refuses every path outside the read-approvals /
 * decide-approval whitelist. The window may surface a pending approval and let the user click
 * approve/reject, but it can never reach a tool, a file, or another session's data.
 *
 * Every failure is a `{status, body}` value; no credential or upstream error text escapes.
 */

const GUID = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';

const routes = [
  { method: 'GET', pattern: '^/api/v1/approvals$' },
  { method: 'GET', pattern: `^/api/v1/approvals/${GUID}$` },
  { method: 'POST', pattern: `^/api/v1/approvals/${GUID}/decision$` },
].map(route => ({ method: route.method, pattern: new RegExp(route.pattern, 'i') }));

const allowedQueryKeys = new Set(['status', 'session_id', 'limit']);
const allowedMethods = new Set(['GET', 'POST']);
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

function createPetAgentApiBridge({
  baseUrl,
  token,
  isSessionPartnerWindowSender,
  fetchImpl = globalThis.fetch,
  timeoutMs = 15000,
  maxBodyBytes = 16 * 1024,
} = {}) {
  if (typeof isSessionPartnerWindowSender !== 'function') throw new Error('A session partner window sender check is required.');
  if (typeof fetchImpl !== 'function') throw new Error('A fetch implementation is required.');
  const origin = typeof baseUrl === 'string' ? baseUrl.replace(/\/+$/, '') : '';

  return async function bridge(event, payload) {
    if (!isSessionPartnerWindowSender(event?.sender)) {
      throw new Error('Pet agent API IPC is only available to a session partner window.');
    }
    if (!origin || typeof token !== 'string' || token.length === 0) {
      return { status: 503, body: { code: 'host_not_ready' } };
    }
    if (!payload || typeof payload !== 'object' || Array.isArray(payload)) {
      return invalid('A pet agent request payload is required.');
    }
    const method = payload.method;
    if (!allowedMethods.has(method)) return invalid('Unsupported pet agent request method.');
    const path = parsePath(payload.path);
    if (!path || !isAllowedRoute(method, path.pathname)) {
      return invalid('Path is not allowed for the session partner window.');
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

module.exports = { createPetAgentApiBridge };