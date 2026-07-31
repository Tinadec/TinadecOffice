import assert from 'node:assert/strict';
import test from 'node:test';
import { authenticate, buildForwardHeaders, verifyJwt, base64urlDecode } from './auth.js';
import type { AuthConfig } from './config.js';

// --- Test helpers: sign JWTs with WebCrypto ---

function base64urlEncode(data: string): string {
  const binary = Buffer.from(data, 'utf-8').toString('base64');
  return binary.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function base64urlEncodeBytes(bytes: Uint8Array): string {
  const binary = Buffer.from(bytes).toString('base64');
  return binary.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

async function signJwt(
  payload: Record<string, unknown>,
  secret: string,
  header: Record<string, unknown> = { alg: 'HS256', typ: 'JWT' },
): Promise<string> {
  const segments = [base64urlEncode(JSON.stringify(header)), base64urlEncode(JSON.stringify(payload))];
  const data = new TextEncoder().encode(`${segments[0]}.${segments[1]}`);
  const key = await crypto.subtle.importKey(
    'raw',
    new TextEncoder().encode(secret),
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign'],
  );
  const sig = new Uint8Array(await crypto.subtle.sign('HMAC', key, data));
  segments.push(base64urlEncodeBytes(sig));
  return segments.join('.');
}

const TEST_SECRET = 'test-jwt-secret-key-for-auth';
const OTHER_SECRET = 'different-secret-key';
const NOW = Math.floor(Date.now() / 1000);

// --- Tests ---

test('verifyJwt: valid HS256 token with correct secret', async () => {
  const token = await signJwt(
    { sub: 'user-1', tenant_id: 'tenant-abc', roles: ['admin'], exp: NOW + 3600 },
    TEST_SECRET,
  );
  const result = await verifyJwt(token, TEST_SECRET);
  assert.ok(result, 'should verify successfully');
  assert.equal(result!.sub, 'user-1');
  assert.equal(result!.tenant_id, 'tenant-abc');
  assert.deepEqual(result!.roles, ['admin']);
});

test('verifyJwt: token signed with different secret is rejected', async () => {
  const token = await signJwt(
    { sub: 'user-1', exp: NOW + 3600 },
    OTHER_SECRET,
  );
  const result = await verifyJwt(token, TEST_SECRET);
  assert.equal(result, null, 'wrong secret should return null');
});

test('verifyJwt: tampered payload is rejected', async () => {
  const token = await signJwt(
    { sub: 'user-1', exp: NOW + 3600 },
    TEST_SECRET,
  );
  // Tamper: replace payload with different data, keep original signature
  const parts = token.split('.');
  const tamperedPayload = base64urlEncode(JSON.stringify({ sub: 'user-2', exp: NOW + 3600 }));
  const tampered = `${parts[0]}.${tamperedPayload}.${parts[2]}`;
  const result = await verifyJwt(tampered, TEST_SECRET);
  assert.equal(result, null, 'tampered payload should be rejected');
});

test('verifyJwt: alg:none token is rejected', async () => {
  const header = base64urlEncode(JSON.stringify({ alg: 'none' }));
  const payload = base64urlEncode(JSON.stringify({ sub: 'user-1', exp: NOW + 3600 }));
  const token = `${header}.${payload}.`;
  const result = await verifyJwt(token, TEST_SECRET);
  assert.equal(result, null, 'alg:none should be rejected');
});

test('verifyJwt: alg:HS512 token is rejected', async () => {
  const token = await signJwt(
    { sub: 'user-1', exp: NOW + 3600 },
    TEST_SECRET,
    { alg: 'HS512', typ: 'JWT' },
  );
  const result = await verifyJwt(token, TEST_SECRET);
  assert.equal(result, null, 'non-HS256 algorithm should be rejected');
});

test('verifyJwt: expired token is rejected', async () => {
  const token = await signJwt(
    { sub: 'user-1', exp: NOW - 3600 },
    TEST_SECRET,
  );
  const result = await verifyJwt(token, TEST_SECRET);
  assert.equal(result, null, 'expired token should be rejected');
});

test('verifyJwt: nbf in the future is rejected', async () => {
  const token = await signJwt(
    { sub: 'user-1', exp: NOW + 7200, nbf: NOW + 3600 },
    TEST_SECRET,
  );
  const result = await verifyJwt(token, TEST_SECRET);
  assert.equal(result, null, 'token not yet valid should be rejected');
});

test('authenticate: cloud mode with no secret + Bearer token is rejected (fail-closed)', async () => {
  const token = await signJwt(
    { sub: 'user-1', tenant_id: 'tenant-abc', exp: NOW + 3600 },
    TEST_SECRET,
  );
  const headers = new Headers({ authorization: `Bearer ${token}` });
  const authConfig: AuthConfig = { required: true };
  // jwtSecret is undefined — cloud mode without configured secret
  const result = await authenticate(headers, authConfig);
  assert.equal(result.ok, false, 'should reject when no secret configured');
  assert.equal(result.error?.code, 'AUTH_INVALID_TOKEN');
});

test('authenticate: local mode (no authConfig) passes through', async () => {
  const headers = new Headers({ authorization: 'Bearer any-token' });
  const result = await authenticate(headers, undefined);
  assert.equal(result.ok, true);
  assert.equal(result.context?.authenticated, true);
});

test('authenticate: valid JWT in cloud mode with correct secret', async () => {
  const token = await signJwt(
    { sub: 'user-1', tenant_id: 'tenant-xyz', roles: ['editor'], exp: NOW + 3600 },
    TEST_SECRET,
  );
  const headers = new Headers({ authorization: `Bearer ${token}` });
  const authConfig: AuthConfig = { required: true, jwtSecret: TEST_SECRET };
  const result = await authenticate(headers, authConfig);
  assert.equal(result.ok, true);
  assert.equal(result.context?.authenticated, true);
  assert.equal(result.context?.userId, 'user-1');
  assert.equal(result.context?.tenantId, 'tenant-xyz');
  assert.deepEqual(result.context?.roles, ['editor']);
});

test('authenticate: API key path is unaffected', async () => {
  const headers = new Headers({ 'x-api-key': 'my-api-key' });
  const authConfig: AuthConfig = {
    required: true,
    apiKeyValidator: (key) => key === 'my-api-key',
  };
  const result = await authenticate(headers, authConfig);
  assert.equal(result.ok, true);
  assert.equal(result.context?.authenticated, true);
  assert.equal(result.context?.tenantId, undefined);
});

test('authenticate: invalid API key is rejected', async () => {
  const headers = new Headers({ 'x-api-key': 'wrong-key' });
  const authConfig: AuthConfig = {
    required: true,
    apiKeyValidator: (key) => key === 'my-api-key',
  };
  const result = await authenticate(headers, authConfig);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, 'AUTH_INVALID_API_KEY');
});

test('authenticate: cloud mode required but no auth header → AUTH_REQUIRED', async () => {
  const headers = new Headers();
  const authConfig: AuthConfig = { required: true, jwtSecret: TEST_SECRET };
  const result = await authenticate(headers, authConfig);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, 'AUTH_REQUIRED');
});

test('buildForwardHeaders: unauthenticated context omits x-tenant-id and x-user-id', () => {
  const headers = buildForwardHeaders({ authenticated: false });
  assert.equal(headers['x-tenant-id'], undefined);
  assert.equal(headers['x-user-id'], undefined);
});

test('buildForwardHeaders: authenticated context injects tenant and user headers', () => {
  const headers = buildForwardHeaders({
    authenticated: true,
    tenantId: 't-1',
    userId: 'u-1',
  });
  assert.equal(headers['x-tenant-id'], 't-1');
  assert.equal(headers['x-user-id'], 'u-1');
});

test('buildForwardHeaders: merges with existing headers', () => {
  const headers = buildForwardHeaders(
    { authenticated: true, tenantId: 't-2', userId: 'u-2' },
    { 'content-type': 'application/json', 'x-custom': 'value' },
  );
  assert.equal(headers['content-type'], 'application/json');
  assert.equal(headers['x-custom'], 'value');
  assert.equal(headers['x-tenant-id'], 't-2');
  assert.equal(headers['x-user-id'], 'u-2');
});

test('base64urlDecode: handles missing padding correctly', () => {
  // "test" in base64 = "dGVzdA==" — base64url = "dGVzdA"
  const result = base64urlDecode('dGVzdA');
  const decoded = new TextDecoder().decode(result);
  assert.equal(decoded, 'test');
});

test('base64urlDecode: handles URL-safe characters', () => {
  // Encode raw bytes that produce + and / in standard base64
  const raw = new Uint8Array([0xfb, 0xef, 0xfe]);
  const base64 = Buffer.from(raw).toString('base64');
  const urlSafe = base64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  const decoded = base64urlDecode(urlSafe);
  assert.deepEqual(Array.from(decoded), [0xfb, 0xef, 0xfe]);
});
