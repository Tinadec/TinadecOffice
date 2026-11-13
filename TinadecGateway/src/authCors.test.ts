import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;
const LOCAL_PAGE = 'http://127.0.0.1:9999';

afterEach(() => {
  globalThis.fetch = originalFetch;
});

function stubCore() {
  globalThis.fetch = (async () =>
    new Response(JSON.stringify({ authorize_url: 'https://logto.example/authorize', state: 's' }), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    })) as typeof fetch;
}

// /auth/* serves only the trusted Electron main process. ALLOWED_ORIGINS deliberately still
// matches any 127.0.0.1 port — the renderer needs it — so the narrowing is per path, not global.
// CORS only stops a browser reading the body; Core's host gate is what refuses the request.
test('auth routes send no CORS headers to a local page', { concurrency: false }, async () => {
  stubCore();
  const response = await app.handle(new Request('http://gateway.local/auth/begin', {
    headers: { origin: LOCAL_PAGE },
  }));
  assert.equal(response.headers.get('access-control-allow-origin'), null);
});

test('api routes still send CORS headers to the same local page', { concurrency: false }, async () => {
  stubCore();
  const response = await app.handle(new Request('http://gateway.local/api/v1/health', {
    headers: { origin: LOCAL_PAGE },
  }));
  assert.equal(response.headers.get('access-control-allow-origin'), LOCAL_PAGE);
});

test('an auth preflight grants nothing', { concurrency: false }, async () => {
  stubCore();
  const response = await app.handle(new Request('http://gateway.local/auth/begin', {
    method: 'OPTIONS',
    headers: {
      origin: LOCAL_PAGE,
      'access-control-request-method': 'GET',
      'access-control-request-headers': 'x-tinadec-host-control',
    },
  }));
  assert.equal(response.status, 204);
  for (const header of ['access-control-allow-origin', 'access-control-allow-methods', 'access-control-allow-headers', 'access-control-max-age']) {
    assert.equal(response.headers.get(header), null, `${header} must not be granted on /auth/*`);
  }
});

test('an api preflight still grants the usual headers', { concurrency: false }, async () => {
  stubCore();
  const response = await app.handle(new Request('http://gateway.local/api/v1/health', {
    method: 'OPTIONS',
    headers: { origin: LOCAL_PAGE, 'access-control-request-method': 'GET' },
  }));
  assert.equal(response.status, 204);
  assert.equal(response.headers.get('access-control-allow-origin'), LOCAL_PAGE);
  assert.equal(response.headers.get('access-control-allow-methods'), 'GET');
});