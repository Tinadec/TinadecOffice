import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, test } from 'node:test';
import { Elysia } from 'elysia';
import {
  authenticateLocalPassword,
  hashPassword,
  LoginRateLimiter,
  verifyPasswordHash,
} from './authProviders/localPassword.js';
import {
  clearCloudflareJwksCache,
  verifyCloudflareAccessAssertion,
} from './authProviders/cloudflareAccess.js';
import {
  completeCoreOidcCallback,
  isOidcIdentityAllowed,
} from './authProviders/officialOidc.js';
import {
  buildCorePrincipalForwardHeaders,
  createWebAuthRuntime,
  evaluateWebAdmission,
  isHttpsRequest,
  isStrictLoopbackRequest,
  resolvePublicOrigin,
  type AuthenticatedPrincipal,
  type ServerLike,
  type WebAuthSettings,
} from './webAuth.js';
import {
  isWebPublicApiPath,
  registerWebServerPlugin,
  tryServeWebStatic,
} from './webServerPlugin.js';

const ORIGINAL_TOKEN = process.env.TINADEC_HOST_CONTROL_TOKEN;
const ORIGINAL_WEB_PASSWORD = process.env.TINADEC_WEB_PASSWORD;

afterEach(() => {
  if (ORIGINAL_TOKEN === undefined) delete process.env.TINADEC_HOST_CONTROL_TOKEN;
  else process.env.TINADEC_HOST_CONTROL_TOKEN = ORIGINAL_TOKEN;

  if (ORIGINAL_WEB_PASSWORD === undefined) delete process.env.TINADEC_WEB_PASSWORD;
  else process.env.TINADEC_WEB_PASSWORD = ORIGINAL_WEB_PASSWORD;

  clearCloudflareJwksCache();
});

function fakeServer(ip: string): ServerLike {
  return { requestIP: () => ({ address: ip }) };
}

function base64url(input: Uint8Array | string): string {
  const buf = typeof input === 'string' ? Buffer.from(input, 'utf8') : Buffer.from(input);
  return buf.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

describe('Phase 10 WebAuth 3-tier admission & providers', () => {
  test('Electron host credential admits as tier=electron and rejects invalid token', async () => {
    process.env.TINADEC_HOST_CONTROL_TOKEN = 'secret-host-token-123';
    const runtime = createWebAuthRuntime();

    const okReq = new Request('http://127.0.0.1:48730/api/v1/projects', {
      headers: {
        host: '127.0.0.1:48730',
        'x-tinadec-host-control': 'secret-host-token-123',
      },
    });
    const okRes = await evaluateWebAdmission({
      request: okReq,
      server: fakeServer('127.0.0.1'),
      runtime,
      mode: 'local',
    });
    assert.equal(okRes.admitted, true);
    if (okRes.admitted) {
      assert.equal(okRes.tier, 'electron');
      assert.equal(okRes.principal.authMethod, 'electron_host');
    }

    const badReq = new Request('http://127.0.0.1:48730/api/v1/projects', {
      headers: {
        host: '127.0.0.1:48730',
        'x-tinadec-host-control': 'wrong-token',
      },
    });
    const badRes = await evaluateWebAdmission({
      request: badReq,
      server: fakeServer('127.0.0.1'),
      runtime,
      mode: 'local',
    });
    assert.equal(badRes.admitted, false);
    if (!badRes.admitted) assert.equal(badRes.status, 403);
  });

  test('Loopback auto-login admits direct localhost browser without proxy headers', async () => {
    const runtime = createWebAuthRuntime();
    for (const ip of ['127.0.0.1', '::1', '::ffff:127.0.0.1']) {
      const req = new Request('http://localhost:48730/api/v1/projects', {
        headers: { host: 'localhost:48730' },
      });
      const res = await evaluateWebAdmission({
        request: req,
        server: fakeServer(ip),
        runtime,
        mode: 'local',
      });
      assert.equal(res.admitted, true, `expected ${ip} loopback to be admitted`);
      if (res.admitted) {
        assert.equal(res.tier, 'loopback');
        assert.equal(res.principal.authMethod, 'loopback_local');
      }
    }
  });

  test('cloudflared proxy traffic from 127.0.0.1 with CF/Forwarded headers NEVER auto-logins', async () => {
    const runtime = createWebAuthRuntime();
    for (const proxyHeader of [
      ['cf-connecting-ip', '203.0.113.5'],
      ['cf-ray', '8abc1234-SJC'],
      ['x-forwarded-for', '203.0.113.5'],
      ['x-real-ip', '203.0.113.5'],
      ['forwarded', 'for=203.0.113.5;proto=https'],
    ] as const) {
      const req = new Request('http://127.0.0.1:48730/api/v1/projects', {
        headers: {
          host: '127.0.0.1:48730',
          [proxyHeader[0]]: proxyHeader[1],
        },
      });
      const res = await evaluateWebAdmission({
        request: req,
        server: fakeServer('127.0.0.1'),
        runtime,
        mode: 'local',
      });
      assert.equal(res.admitted, false, `${proxyHeader[0]} must block loopback auto-login`);
      assert.equal(res.tier, 'remote');
      if (!res.admitted) assert.equal(res.status, 401);
      assert.equal(isStrictLoopbackRequest(req, fakeServer('127.0.0.1')), false);
    }
  });

  test('Remote LAN IP and non-loopback Host header fail closed with 401', async () => {
    const runtime = createWebAuthRuntime();
    const lanReq = new Request('http://192.168.1.25:48730/api/v1/projects', {
      headers: { host: '192.168.1.25:48730' },
    });
    const lanRes = await evaluateWebAdmission({
      request: lanReq,
      server: fakeServer('192.168.1.25'),
      runtime,
      mode: 'local',
    });
    assert.equal(lanRes.admitted, false);
    assert.equal(lanRes.tier, 'remote');

    const rebindReq = new Request('http://evil.local:48730/api/v1/projects', {
      headers: { host: 'evil.local:48730' },
    });
    const rebindRes = await evaluateWebAdmission({
      request: rebindReq,
      server: fakeServer('127.0.0.1'),
      runtime,
      mode: 'local',
    });
    assert.equal(rebindRes.admitted, false);
  });

  test('Loopback mutating requests reject cross-site Origin/Sec-Fetch-Site and allow Vite dev origin', async () => {
    const runtime = createWebAuthRuntime();
    const csrfReq = new Request('http://127.0.0.1:48730/api/v1/projects', {
      method: 'POST',
      headers: {
        host: '127.0.0.1:48730',
        origin: 'https://evil.example',
        'sec-fetch-site': 'cross-site',
      },
    });
    const csrfRes = await evaluateWebAdmission({
      request: csrfReq,
      server: fakeServer('127.0.0.1'),
      runtime,
      mode: 'local',
    });
    assert.equal(csrfRes.admitted, false);
    if (!csrfRes.admitted) assert.equal(csrfRes.status, 403);

    const viteReq = new Request('http://127.0.0.1:48730/api/v1/projects', {
      method: 'POST',
      headers: {
        host: '127.0.0.1:48730',
        origin: 'http://127.0.0.1:5173',
        'sec-fetch-site': 'same-origin',
      },
    });
    const viteRes = await evaluateWebAdmission({
      request: viteReq,
      server: fakeServer('127.0.0.1'),
      runtime,
      mode: 'local',
    });
    assert.equal(viteRes.admitted, true);
  });

  test('buildCorePrincipalForwardHeaders overwrites spoofed identity headers and injects server token', () => {
    process.env.TINADEC_HOST_CONTROL_TOKEN = 'server-secret';
    const principal: AuthenticatedPrincipal = {
      userId: 'owner-1',
      tenantId: 'default',
      roles: ['owner'],
      authMethod: 'local_password',
    };
    const forwarded = buildCorePrincipalForwardHeaders(
      {
        'x-request-id': 'req-1',
        'x-user-id': 'spoofed',
        'x-tenant-id': 'spoofed-tenant',
        'x-roles': 'superadmin',
        'x-tinadec-host-control': 'spoofed-token',
      },
      principal,
    );
    assert.equal(forwarded['x-tinadec-host-control'], 'server-secret');
    assert.equal(forwarded['x-user-id'], 'owner-1');
    assert.equal(forwarded['x-tenant-id'], 'default');
    assert.equal(forwarded['x-roles'], 'owner');
    assert.equal(forwarded['x-auth-method'], 'local_password');
  });

  test('local_password scrypt hashing + brute-force rate limiter', () => {
    const hash = hashPassword('correct-horse-battery');
    assert.ok(hash.startsWith('scrypt$'));
    assert.equal(verifyPasswordHash('correct-horse-battery', hash), true);
    assert.equal(verifyPasswordHash('wrong-password', hash), false);

    const limiter = new LoginRateLimiter(3, 60_000, 60_000);
    for (let i = 0; i < 2; i++) {
      const fail = authenticateLocalPassword({
        password: 'bad',
        storedHash: hash,
        clientKey: '203.0.113.10',
        rateLimiter: limiter,
      });
      assert.equal(fail.ok, false);
      if (!fail.ok) assert.equal(fail.status, 401);
    }

    const locked = authenticateLocalPassword({
      password: 'bad',
      storedHash: hash,
      clientKey: '203.0.113.10',
      rateLimiter: limiter,
    });
    assert.equal(locked.ok, false);
    if (!locked.ok) {
      assert.equal(locked.status, 429);
      assert.ok((locked.retryAfterSeconds ?? 0) > 0);
    }

    // Even the right password is blocked while locked out.
    const blockedGood = authenticateLocalPassword({
      password: 'correct-horse-battery',
      storedHash: hash,
      clientKey: '203.0.113.10',
      rateLimiter: limiter,
    });
    assert.equal(blockedGood.ok, false);
    if (!blockedGood.ok) assert.equal(blockedGood.status, 429);
  });

  test('official_oidc allowlist and Core exchange bridge', async () => {
    const settings: WebAuthSettings = {
      allow_loopback_auto_login: true,
      oidc_enabled: true,
      allowed_subjects: ['sub-owner'],
      allowed_emails: ['owner@example.com'],
      cloudflare_access_enabled: false,
      session_ttl_seconds: 3600,
      updated_at: new Date().toISOString(),
    };

    assert.equal(isOidcIdentityAllowed({ subject: 'sub-owner', email: 'other@example.com' }, settings), true);
    assert.equal(isOidcIdentityAllowed({ subject: 'other-sub', email: 'OWNER@example.com' }, settings), true);
    assert.equal(isOidcIdentityAllowed({ subject: 'stranger', email: 'stranger@example.com' }, settings), false);

    const forbidden = await completeCoreOidcCallback({
      coreUrl: 'http://127.0.0.1:48731',
      query: new URLSearchParams({ code: 'abc', state: 'st-1' }),
      settings,
      hostControlToken: 'host-tok',
      fetchImpl: (async () =>
        Response.json({
          session: 'sess-1',
          identity: { subject: 'stranger', email: 'stranger@example.com', nickname: 'Stranger' },
        })) as typeof fetch,
    });
    assert.equal(forbidden.ok, false);
    if (!forbidden.ok) assert.equal(forbidden.status, 403);

    const allowed = await completeCoreOidcCallback({
      coreUrl: 'http://127.0.0.1:48731',
      query: new URLSearchParams({ code: 'abc', state: 'st-2' }),
      settings,
      hostControlToken: 'host-tok',
      fetchImpl: (async () =>
        Response.json({
          session: 'sess-2',
          identity: { subject: 'sub-owner', email: 'owner@example.com', nickname: 'Owner' },
        })) as typeof fetch,
    });
    assert.equal(allowed.ok, true);
    if (allowed.ok) {
      assert.equal(allowed.principal.userId, 'sub-owner');
      assert.equal(allowed.principal.authMethod, 'official_oidc');
    }
  });

  test('cloudflare_access verifies RS256 CF-Access-Jwt-Assertion against JWKS', async () => {
    const keyPair = await crypto.subtle.generateKey(
      {
        name: 'RSASSA-PKCS1-v1_5',
        modulusLength: 2048,
        publicExponent: new Uint8Array([1, 0, 1]),
        hash: 'SHA-256',
      },
      true,
      ['sign', 'verify'],
    );
    const pubJwk = (await crypto.subtle.exportKey('jwk', keyPair.publicKey)) as JsonWebKey;
    const nowSec = Math.floor(Date.now() / 1000);
    const header = base64url(JSON.stringify({ alg: 'RS256', kid: 'cf-key-1', typ: 'JWT' }));
    const payload = base64url(
      JSON.stringify({
        iss: 'https://myteam.cloudflareaccess.com',
        aud: ['aud-tag-123'],
        sub: 'cf-user-1',
        email: 'owner@example.com',
        exp: nowSec + 300,
        nbf: nowSec - 10,
      }),
    );
    const sig = await crypto.subtle.sign(
      'RSASSA-PKCS1-v1_5',
      keyPair.privateKey,
      new TextEncoder().encode(`${header}.${payload}`),
    );
    const jwt = `${header}.${payload}.${base64url(new Uint8Array(sig))}`;

    const settings: WebAuthSettings = {
      allow_loopback_auto_login: true,
      oidc_enabled: false,
      allowed_subjects: [],
      allowed_emails: ['owner@example.com'],
      cloudflare_access_enabled: true,
      cloudflare_access_team_domain: 'myteam.cloudflareaccess.com',
      cloudflare_access_aud: 'aud-tag-123',
      session_ttl_seconds: 3600,
      updated_at: new Date().toISOString(),
    };

    const mockFetch = (async () =>
      Response.json({ keys: [{ ...pubJwk, kid: 'cf-key-1', alg: 'RS256', use: 'sig' }] })) as typeof fetch;

    const ok = await verifyCloudflareAccessAssertion({
      assertion: jwt,
      settings,
      fetchImpl: mockFetch,
      nowSeconds: nowSec,
    });
    assert.equal(ok.ok, true);
    if (ok.ok) {
      assert.equal(ok.principal.authMethod, 'cloudflare_access');
      assert.equal(ok.principal.email, 'owner@example.com');
    }

    const badAud = await verifyCloudflareAccessAssertion({
      assertion: jwt,
      settings: { ...settings, cloudflare_access_aud: 'other-aud' },
      fetchImpl: mockFetch,
      nowSeconds: nowSec,
    });
    assert.equal(badAud.ok, false);
  });

  test('Cloudflare trustProxy resolves https origin and Secure cookie flag', () => {
    const req = new Request('http://127.0.0.1:48730/api/v1/web-auth/oidc/login', {
      headers: {
        host: '127.0.0.1:48730',
        'x-forwarded-proto': 'https',
        'x-forwarded-host': 'tinadec.example.com',
      },
    });
    assert.equal(isHttpsRequest(req, 'cloudflare'), true);
    assert.equal(resolvePublicOrigin(req, 'cloudflare'), 'https://tinadec.example.com');
    assert.equal(isHttpsRequest(req, 'false'), false);
    assert.equal(resolvePublicOrigin(req, 'false'), 'http://127.0.0.1:48730');
  });

  test('Elysia plugin routes: login, cookie session, protected settings, logout, and static SPA serving', async () => {
    const tempDir = mkdtempSync(join(tmpdir(), 'tinadec-web-dist-'));
    try {
      mkdirSync(join(tempDir, 'assets'), { recursive: true });
      writeFileSync(join(tempDir, 'index.html'), '<!doctype html><html><body>WebApp</body></html>', 'utf8');
      writeFileSync(join(tempDir, 'assets', 'app.js'), 'console.log("ok")', 'utf8');

      const runtime = createWebAuthRuntime();
      runtime.settingsStore.update({
        password_hash: hashPassword('SuperSecretPass!'),
        allow_loopback_auto_login: false,
      });

      const principalMap = new WeakMap<Request, AuthenticatedPrincipal>();
      const pluginApp = new Elysia()
        .onRequest(async ({ request, set, server }) => {
          const path = new URL(request.url).pathname;
          if (isWebPublicApiPath(path)) return;
          const admission = await evaluateWebAdmission({
            request,
            server: server as ServerLike | null,
            runtime,
            mode: 'local',
            trustProxy: 'cloudflare',
          });
          if (!admission.admitted) {
            set.status = admission.status;
            return { code: admission.code, message: admission.message };
          }
          principalMap.set(request, admission.principal);
        });

      registerWebServerPlugin(pluginApp, {
        runtime,
        getPrincipal: (req) => principalMap.get(req),
        forwardHeaders: (req) => buildCorePrincipalForwardHeaders({}, principalMap.get(req)),
        mode: 'local',
        trustProxy: 'cloudflare',
        webDistDir: tempDir,
      });

      // 1. Unauthenticated remote status returns auth_required=true.
      const statusBefore = await pluginApp.handle(
        new Request('http://tinadec.example.com/api/v1/web-auth/status', {
          headers: { host: 'tinadec.example.com', 'cf-connecting-ip': '203.0.113.1' },
        }),
      );
      assert.equal(statusBefore.status, 200);
      const statusJson = (await statusBefore.json()) as { authenticated: boolean; auth_required: boolean; available_providers: string[] };
      assert.equal(statusJson.authenticated, false);
      assert.equal(statusJson.auth_required, true);
      assert.deepEqual(statusJson.available_providers, ['local_password']);

      // 2. Unauthenticated settings read is rejected with 401.
      const settingsDenied = await pluginApp.handle(
        new Request('http://tinadec.example.com/api/v1/web-auth/settings', {
          headers: { host: 'tinadec.example.com', 'cf-connecting-ip': '203.0.113.1' },
        }),
      );
      assert.equal(settingsDenied.status, 401);

      // 3. Login behind Cloudflare HTTPS sets HttpOnly + Secure + SameSite=Lax cookie.
      const loginRes = await pluginApp.handle(
        new Request('http://tinadec.example.com/api/v1/web-auth/login', {
          method: 'POST',
          headers: {
            host: 'tinadec.example.com',
            'content-type': 'application/json',
            'x-forwarded-proto': 'https',
            'cf-connecting-ip': '203.0.113.1',
          },
          body: JSON.stringify({ provider: 'local_password', password: 'SuperSecretPass!' }),
        }),
      );
      assert.equal(loginRes.status, 200);
      const setCookie = loginRes.headers.get('set-cookie') ?? '';
      assert.match(setCookie, /tinadec_session=/);
      assert.match(setCookie, /HttpOnly/);
      assert.match(setCookie, /SameSite=Lax/);
      assert.match(setCookie, /Secure/);
      const cookiePair = setCookie.split(';')[0]!;

      // 4. Authenticated settings read & update succeed.
      const settingsRes = await pluginApp.handle(
        new Request('http://tinadec.example.com/api/v1/web-auth/settings', {
          headers: {
            host: 'tinadec.example.com',
            cookie: cookiePair,
            'cf-connecting-ip': '203.0.113.1',
          },
        }),
      );
      assert.equal(settingsRes.status, 200);
      const settingsJson = (await settingsRes.json()) as { password_configured: boolean; allow_loopback_auto_login: boolean };
      assert.equal(settingsJson.password_configured, true);
      assert.equal(settingsJson.allow_loopback_auto_login, false);

      // 5. Static serving serves SPA HTML & blocks traversal.
      const spaRes = tryServeWebStatic(new Request('http://tinadec.example.com/settings'), tempDir);
      assert.ok(spaRes);
      assert.equal(spaRes!.status, 200);
      assert.match(await spaRes!.text(), /WebApp/);

      const traversalRes = tryServeWebStatic(new Request('http://tinadec.example.com/assets/..%5c..%5csecret'), tempDir);
      assert.ok(traversalRes);
      assert.equal(traversalRes!.status, 403);

      // 6. Logout revokes session.
      const logoutRes = await pluginApp.handle(
        new Request('http://tinadec.example.com/api/v1/web-auth/logout', {
          method: 'POST',
          headers: { host: 'tinadec.example.com', cookie: cookiePair },
        }),
      );
      assert.equal(logoutRes.status, 200);

      const afterLogout = await pluginApp.handle(
        new Request('http://tinadec.example.com/api/v1/web-auth/settings', {
          headers: { host: 'tinadec.example.com', cookie: cookiePair },
        }),
      );
      assert.equal(afterLogout.status, 401);
    } finally {
      rmSync(tempDir, { recursive: true, force: true });
    }
  });
});
