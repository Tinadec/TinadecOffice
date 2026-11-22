import { existsSync, statSync } from 'node:fs';
import { extname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { t } from 'elysia';
import type { AuthConfig, DeployMode } from './config.js';
import { coreUrl } from './coreClient.js';
import { toProblemDetails } from './mappers/errorMapper.js';
import {
  authenticateLocalPassword,
  hashPassword,
  validateNewPassword,
} from './authProviders/localPassword.js';
import {
  beginCoreOidcLogin,
  completeCoreOidcCallback,
  sanitizeReturnTo,
  WebOidcBridge,
} from './authProviders/officialOidc.js';
import type { AuthProviderId } from './authProviders/types.js';
import {
  buildClearSessionCookieHeader,
  buildSessionCookieHeader,
  evaluateWebAdmission,
  getClientIpForRateLimit,
  isHttpsRequest,
  parseCookies,
  resolvePublicOrigin,
  resolveTrustProxyMode,
  SESSION_COOKIE_NAME,
  type AuthenticatedPrincipal,
  type ServerLike,
  type TrustProxyMode,
  type WebAuthRuntime,
} from './webAuth.js';
import {
  createAgentTerminalBridgeSession,
  createInteractiveShellSession,
  detectHostShellProfiles,
  type WsTerminalClient,
  type WsTerminalSession,
} from './wsTerminalProxy.js';

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type AnyElysia = any;

export interface WebServerPluginOptions {
  runtime: WebAuthRuntime;
  getPrincipal: (request: Request) => AuthenticatedPrincipal | undefined;
  forwardHeaders: (request: Request) => Record<string, string>;
  mode: DeployMode;
  authConfig?: AuthConfig;
  trustProxy?: TrustProxyMode;
  webDistDir?: string;
}

const MIME_TYPES: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.mjs': 'application/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.gif': 'image/gif',
  '.ico': 'image/x-icon',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
  '.ttf': 'font/ttf',
  '.wasm': 'application/wasm',
  '.map': 'application/json; charset=utf-8',
};

const WEB_PUBLIC_PREFIXES = [
  '/api/v1/health',
  '/api/v1/host-challenge',
  '/api/v1/web-auth/status',
  '/api/v1/web-auth/login',
  '/api/v1/web-auth/logout',
  '/api/v1/web-auth/oidc/login',
  '/api/v1/web-auth/oidc/callback',
  '/docs',
];

export function isWebPublicApiPath(pathname: string): boolean {
  if (pathname.startsWith('/auth/')) return true;
  for (const prefix of WEB_PUBLIC_PREFIXES) {
    if (pathname === prefix || pathname.startsWith(`${prefix}/`)) return true;
  }
  return !pathname.startsWith('/api/') && !pathname.startsWith('/ws/');
}

export function defaultWebDistDir(): string {
  const fromEnv = process.env.TINADEC_WEB_DIST_DIR?.trim();
  if (fromEnv) return resolve(fromEnv);
  const here = fileURLToPath(new URL('.', import.meta.url));
  return resolve(here, '../../apps/web/dist');
}

function serializePrincipal(principal?: AuthenticatedPrincipal | null) {
  if (!principal) return null;
  return {
    user_id: principal.userId,
    tenant_id: principal.tenantId,
    roles: principal.roles,
    auth_method: principal.authMethod,
    ...(principal.displayName ? { display_name: principal.displayName } : {}),
    ...(principal.email ? { email: principal.email } : {}),
    ...(principal.avatarUrl ? { avatar_url: principal.avatarUrl } : {}),
  };
}

function sanitizeSettingsResponse(
  runtime: WebAuthRuntime,
  trustProxy: TrustProxyMode,
) {
  const s = runtime.settingsStore.get();
  return {
    allow_loopback_auto_login: s.allow_loopback_auto_login,
    password_configured: Boolean(s.password_hash || process.env.TINADEC_WEB_PASSWORD),
    oidc_enabled: s.oidc_enabled,
    allowed_subjects: s.allowed_subjects,
    allowed_emails: s.allowed_emails,
    cloudflare_access_enabled: s.cloudflare_access_enabled,
    cloudflare_access_team_domain: s.cloudflare_access_team_domain ?? '',
    cloudflare_access_aud: s.cloudflare_access_aud ?? '',
    session_ttl_seconds: s.session_ttl_seconds,
    trust_proxy: trustProxy,
    updated_at: s.updated_at,
  };
}

function availableProviders(runtime: WebAuthRuntime, trustProxy: TrustProxyMode): AuthProviderId[] {
  const s = runtime.settingsStore.get();
  const providers: AuthProviderId[] = [];
  if (s.password_hash || process.env.TINADEC_WEB_PASSWORD) {
    providers.push('local_password');
  }
  if (s.oidc_enabled) {
    providers.push('official_oidc');
  }
  if (s.cloudflare_access_enabled && trustProxy !== 'false') {
    providers.push('cloudflare_access');
  }
  return providers;
}

export function tryServeWebStatic(request: Request, distDir: string): Response | null {
  if (request.method !== 'GET' && request.method !== 'HEAD') return null;
  const pathname = new URL(request.url).pathname;
  if (
    pathname.startsWith('/api/') ||
    pathname === '/api' ||
    pathname.startsWith('/auth/') ||
    pathname === '/auth' ||
    pathname.startsWith('/ws/') ||
    pathname === '/ws' ||
    pathname === '/docs' ||
    pathname.startsWith('/docs/')
  ) {
    return null;
  }

  const indexPath = join(distDir, 'index.html');
  if (!existsSync(indexPath)) return null;

  let decodedRel: string;
  try {
    if (/(?:^|\/)%2e%2e(?:\/|%5c|$)/i.test(request.url)) {
      return new Response('Forbidden', { status: 403 });
    }
    decodedRel = decodeURIComponent(pathname).replace(/^\/+/, '');
  } catch {
    return new Response('Bad Request', { status: 400 });
  }

  if (decodedRel.split(/[\\/]/).includes('..')) {
    return new Response('Forbidden', { status: 403 });
  }

  const resolvedDist = resolve(distDir);
  const candidate = decodedRel ? resolve(resolvedDist, decodedRel) : indexPath;
  if (candidate !== resolvedDist && !candidate.startsWith(resolvedDist + sep)) {
    return new Response('Forbidden', { status: 403 });
  }

  let targetFile: string | null = null;
  if (existsSync(candidate)) {
    try {
      const st = statSync(candidate);
      if (st.isFile()) targetFile = candidate;
      else if (st.isDirectory()) {
        const dirIndex = join(candidate, 'index.html');
        if (existsSync(dirIndex) && statSync(dirIndex).isFile()) targetFile = dirIndex;
      }
    } catch {
      targetFile = null;
    }
  }

  // SPA fallback for client routes without a file extension.
  if (!targetFile && !extname(decodedRel)) {
    targetFile = indexPath;
  }
  if (!targetFile) return null;

  const ext = extname(targetFile).toLowerCase();
  const contentType = MIME_TYPES[ext] ?? 'application/octet-stream';
  const isImmutableAsset = decodedRel.startsWith('assets/') && ext !== '.html';
  const cacheControl = isImmutableAsset ? 'public, max-age=31536000, immutable' : 'no-cache';

  const file = Bun.file(targetFile);
  return new Response(request.method === 'HEAD' ? null : file, {
    status: 200,
    headers: {
      'content-type': contentType,
      'cache-control': cacheControl,
    },
  });
}

export function registerWebServerPlugin(app: AnyElysia, options: WebServerPluginOptions): AnyElysia {
  const { runtime, forwardHeaders, mode, authConfig } = options;
  const trustProxy = options.trustProxy ?? resolveTrustProxyMode();
  const distDir = options.webDistDir ?? defaultWebDistDir();
  const oidcBridge = new WebOidcBridge();
  const activeWsTerminals = new Map<unknown, WsTerminalSession>();

  return app
    .get(
      '/api/v1/web-auth/status',
      async ({ request, server }: { request: Request; server?: ServerLike | null }) => {
        const admission = await evaluateWebAdmission({
          request,
          server,
          runtime,
          mode,
          authConfig,
          trustProxy,
        });
        const settings = runtime.settingsStore.get();
        return {
          authenticated: admission.admitted,
          auth_required: !admission.admitted,
          tier: admission.tier,
          principal: admission.admitted ? serializePrincipal(admission.principal) : null,
          available_providers: availableProviders(runtime, trustProxy),
          allow_loopback_auto_login: settings.allow_loopback_auto_login,
          password_configured: Boolean(settings.password_hash || process.env.TINADEC_WEB_PASSWORD),
          oidc_enabled: settings.oidc_enabled,
          cloudflare_access_enabled: settings.cloudflare_access_enabled && trustProxy !== 'false',
          trust_proxy: trustProxy,
        };
      },
      {
        detail: {
          summary: 'Inspect web admission status and available authentication providers',
          tags: ['System'],
          security: [],
        },
      },
    )
    .post(
      '/api/v1/web-auth/login',
      ({
        body,
        request,
        set,
        server,
      }: {
        body: { provider?: string; password?: string };
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const provider = body?.provider ?? 'local_password';
        if (provider !== 'local_password') {
          set.status = 400;
          set.headers['content-type'] = 'application/problem+json';
          return toProblemDetails(
            400,
            'unsupported_auth_provider',
            `Unsupported direct login provider: ${provider}.`,
            '/api/v1/web-auth/login',
          );
        }

        const settings = runtime.settingsStore.get();
        const clientKey = getClientIpForRateLimit(request, server, trustProxy);
        const result = authenticateLocalPassword({
          password: typeof body?.password === 'string' ? body.password : '',
          storedHash: settings.password_hash,
          envPassword: process.env.TINADEC_WEB_PASSWORD,
          clientKey,
          rateLimiter: runtime.rateLimiter,
        });

        if (!result.ok) {
          set.status = result.status;
          set.headers['content-type'] = 'application/problem+json';
          if (result.retryAfterSeconds) {
            set.headers['retry-after'] = String(result.retryAfterSeconds);
          }
          return toProblemDetails(result.status, result.code, result.message, '/api/v1/web-auth/login');
        }

        const token = runtime.sessionStore.create(result.principal, settings.session_ttl_seconds);
        set.headers['set-cookie'] = buildSessionCookieHeader(
          token,
          settings.session_ttl_seconds,
          isHttpsRequest(request, trustProxy),
        );
        return {
          authenticated: true,
          principal: serializePrincipal(result.principal),
        };
      },
      {
        body: t.Object({
          provider: t.Optional(t.String()),
          password: t.Optional(t.String()),
        }),
        detail: {
          summary: 'Authenticate web browser session with owner credentials',
          tags: ['System'],
          security: [],
        },
      },
    )
    .post(
      '/api/v1/web-auth/logout',
      ({
        request,
        set,
      }: {
        request: Request;
        set: { headers: Record<string, string | number> };
      }) => {
        const cookies = parseCookies(request.headers.get('cookie'));
        runtime.sessionStore.revoke(cookies[SESSION_COOKIE_NAME]);
        set.headers['set-cookie'] = buildClearSessionCookieHeader(isHttpsRequest(request, trustProxy));
        return { authenticated: false };
      },
      {
        detail: {
          summary: 'Clear the current web browser session',
          tags: ['System'],
          security: [],
        },
      },
    )
    .get(
      '/api/v1/web-auth/settings',
      ({
        request,
        set,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
      }) => {
        const principal = options.getPrincipal(request);
        if (!principal) {
          set.status = 401;
          set.headers['content-type'] = 'application/problem+json';
          return toProblemDetails(401, 'web_auth_required', 'Authentication is required.', '/api/v1/web-auth/settings');
        }
        if (!principal.roles.includes('owner')) {
          set.status = 403;
          set.headers['content-type'] = 'application/problem+json';
          return toProblemDetails(403, 'forbidden', 'Owner role required.', '/api/v1/web-auth/settings');
        }
        return sanitizeSettingsResponse(runtime, trustProxy);
      },
      {
        detail: {
          summary: 'Read web access & authentication settings (owner only)',
          tags: ['System'],
        },
      },
    )
    .put(
      '/api/v1/web-auth/settings',
      ({
        body,
        request,
        set,
      }: {
        body: {
          allow_loopback_auto_login?: boolean;
          new_password?: string;
          clear_password?: boolean;
          oidc_enabled?: boolean;
          allowed_subjects?: string[];
          allowed_emails?: string[];
          cloudflare_access_enabled?: boolean;
          cloudflare_access_team_domain?: string;
          cloudflare_access_aud?: string;
          session_ttl_seconds?: number;
          revoke_existing_sessions?: boolean;
        };
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
      }) => {
        const principal = options.getPrincipal(request);
        if (!principal) {
          set.status = 401;
          set.headers['content-type'] = 'application/problem+json';
          return toProblemDetails(401, 'web_auth_required', 'Authentication is required.', '/api/v1/web-auth/settings');
        }
        if (!principal.roles.includes('owner')) {
          set.status = 403;
          set.headers['content-type'] = 'application/problem+json';
          return toProblemDetails(403, 'forbidden', 'Owner role required.', '/api/v1/web-auth/settings');
        }

        const patch: Record<string, unknown> = {};
        if (typeof body.allow_loopback_auto_login === 'boolean') {
          patch.allow_loopback_auto_login = body.allow_loopback_auto_login;
        }
        if (typeof body.new_password === 'string' && body.new_password.length > 0) {
          const valid = validateNewPassword(body.new_password);
          if (!valid.ok) {
            set.status = 400;
            set.headers['content-type'] = 'application/problem+json';
            return toProblemDetails(400, 'invalid_password', valid.message, '/api/v1/web-auth/settings');
          }
          patch.password_hash = hashPassword(body.new_password);
        } else if (body.clear_password === true) {
          patch.password_hash = undefined;
        }
        if (typeof body.oidc_enabled === 'boolean') patch.oidc_enabled = body.oidc_enabled;
        if (Array.isArray(body.allowed_subjects)) patch.allowed_subjects = body.allowed_subjects;
        if (Array.isArray(body.allowed_emails)) patch.allowed_emails = body.allowed_emails;
        if (typeof body.cloudflare_access_enabled === 'boolean') {
          patch.cloudflare_access_enabled = body.cloudflare_access_enabled;
        }
        if (typeof body.cloudflare_access_team_domain === 'string') {
          patch.cloudflare_access_team_domain = body.cloudflare_access_team_domain.trim() || undefined;
        }
        if (typeof body.cloudflare_access_aud === 'string') {
          patch.cloudflare_access_aud = body.cloudflare_access_aud.trim() || undefined;
        }
        if (typeof body.session_ttl_seconds === 'number' && Number.isFinite(body.session_ttl_seconds)) {
          patch.session_ttl_seconds = Math.max(300, Math.min(30 * 24 * 3600, Math.floor(body.session_ttl_seconds)));
        }

        runtime.settingsStore.update(patch);
        if (body.revoke_existing_sessions) {
          runtime.sessionStore.revokeAll();
        }
        return sanitizeSettingsResponse(runtime, trustProxy);
      },
      {
        body: t.Object({
          allow_loopback_auto_login: t.Optional(t.Boolean()),
          new_password: t.Optional(t.String()),
          clear_password: t.Optional(t.Boolean()),
          oidc_enabled: t.Optional(t.Boolean()),
          allowed_subjects: t.Optional(t.Array(t.String())),
          allowed_emails: t.Optional(t.Array(t.String())),
          cloudflare_access_enabled: t.Optional(t.Boolean()),
          cloudflare_access_team_domain: t.Optional(t.String()),
          cloudflare_access_aud: t.Optional(t.String()),
          session_ttl_seconds: t.Optional(t.Number()),
          revoke_existing_sessions: t.Optional(t.Boolean()),
        }),
        detail: {
          summary: 'Update web access & authentication settings (owner only)',
          tags: ['System'],
        },
      },
    )
    .get(
      '/api/v1/web-auth/oidc/login',
      async ({
        request,
        set,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
      }) => {
        const settings = runtime.settingsStore.get();
        if (!settings.oidc_enabled) {
          set.status = 503;
          set.headers['content-type'] = 'application/problem+json';
          return toProblemDetails(503, 'oidc_not_configured', 'Official OIDC login is not enabled.', '/api/v1/web-auth/oidc/login');
        }
        const url = new URL(request.url);
        const returnTo = sanitizeReturnTo(url.searchParams.get('return_to'));
        const publicOrigin = resolvePublicOrigin(request, trustProxy);
        const redirectUri = `${publicOrigin}/api/v1/web-auth/oidc/callback`;

        const started = await beginCoreOidcLogin({
          coreUrl: coreUrl(),
          redirectUri,
          settings,
          hostControlToken: process.env.TINADEC_HOST_CONTROL_TOKEN,
        });
        if (!started.ok) {
          set.status = started.status;
          set.headers['content-type'] = 'application/problem+json';
          return toProblemDetails(started.status, started.code, started.message, '/api/v1/web-auth/oidc/login');
        }

        oidcBridge.recordPending(started.state, redirectUri, returnTo);
        return new Response(null, {
          status: 302,
          headers: { location: started.authorizeUrl, 'cache-control': 'no-store' },
        });
      },
      {
        detail: {
          summary: 'Initiate OIDC login redirect for web browser sessions',
          tags: ['System'],
          security: [],
        },
      },
    )
    .get(
      '/api/v1/web-auth/oidc/callback',
      async ({
        request,
        set,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
      }) => {
        const url = new URL(request.url);
        const state = url.searchParams.get('state')?.trim() ?? '';
        const pending = state ? oidcBridge.consumePending(state) : null;
        const settings = runtime.settingsStore.get();

        const completed = await completeCoreOidcCallback({
          coreUrl: coreUrl(),
          query: url.searchParams,
          settings,
          redirectUri: pending?.redirectUri,
          hostControlToken: process.env.TINADEC_HOST_CONTROL_TOKEN,
        });

        if (!completed.ok) {
          set.status = completed.status;
          set.headers['content-type'] = 'application/problem+json';
          return toProblemDetails(
            completed.status,
            completed.code,
            completed.message,
            '/api/v1/web-auth/oidc/callback',
          );
        }

        const token = runtime.sessionStore.create(completed.principal, settings.session_ttl_seconds);
        const cookie = buildSessionCookieHeader(
          token,
          settings.session_ttl_seconds,
          isHttpsRequest(request, trustProxy),
        );
        return new Response(null, {
          status: 302,
          headers: {
            location: pending?.returnTo ?? '/',
            'set-cookie': cookie,
            'cache-control': 'no-store',
          },
        });
      },
      {
        detail: {
          summary: 'Complete OIDC login callback and establish BFF session cookie',
          tags: ['System'],
          security: [],
        },
      },
    )
    .get(
      '/api/v1/web-terminal/shells',
      () => ({ shells: detectHostShellProfiles() }),
      {
        detail: {
          summary: 'List host shell profiles available to the web terminal',
          tags: ['System'],
        },
      },
    )
    .ws('/ws/terminal', {
      async beforeHandle({
        request,
        set,
        server,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) {
        const admission = await evaluateWebAdmission({
          request,
          server,
          runtime,
          mode,
          authConfig,
          trustProxy,
        });
        if (!admission.admitted) {
          set.status = admission.status;
          set.headers['content-type'] = 'application/problem+json';
          return toProblemDetails(admission.status, admission.code, admission.message, '/ws/terminal');
        }
      },
      open(ws: {
        data?: { request?: Request };
        send(data: string): void;
        close(code?: number, reason?: string): void;
      }) {
        const request = ws.data?.request;
        const url = request ? new URL(request.url) : new URL('http://127.0.0.1/ws/terminal');
        const terminalSessionId = url.searchParams.get('terminal_session_id')?.trim();
        const client: WsTerminalClient = {
          send: (data) => ws.send(data),
          close: (code, reason) => ws.close(code, reason),
        };
        const session = terminalSessionId
          ? createAgentTerminalBridgeSession({
              ws: client,
              terminalSessionId,
              runId: url.searchParams.get('run_id'),
              forwardHeaders: request ? forwardHeaders(request) : {},
            })
          : createInteractiveShellSession({
              ws: client,
              shellId: url.searchParams.get('shell'),
              cwd: url.searchParams.get('cwd'),
            });
        activeWsTerminals.set(ws, session);
      },
      message(ws: unknown, message: string | Buffer | Uint8Array) {
        activeWsTerminals.get(ws)?.onMessage(message);
      },
      close(ws: unknown) {
        const session = activeWsTerminals.get(ws);
        if (session) {
          session.close();
          activeWsTerminals.delete(ws);
        }
      },
    })
    .get(
      '/*',
      ({ request, set }: { request: Request; set: { status?: number | string } }) => {
        const staticRes = tryServeWebStatic(request, distDir);
        if (staticRes) return staticRes;
        set.status = 404;
        return toProblemDetails(404, 'NOT_FOUND', `Route ${new URL(request.url).pathname} not found.`, new URL(request.url).pathname);
      },
      { detail: { hide: true } as never },
    );
}
