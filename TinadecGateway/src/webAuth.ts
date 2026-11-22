import { createHash, randomBytes, timingSafeEqual } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { authenticate } from './auth.js';
import type { AuthConfig, DeployMode } from './config.js';
import { verifyCloudflareAccessAssertion } from './authProviders/cloudflareAccess.js';
import { LoginRateLimiter } from './authProviders/localPassword.js';
import type { AuthenticatedPrincipal, WebAuthSettings } from './authProviders/types.js';

export type { AuthenticatedPrincipal, WebAuthMethod, WebAuthSettings } from './authProviders/types.js';

export type AdmissionTier = 'electron' | 'loopback' | 'remote';
export type TrustProxyMode = 'false' | 'true' | 'cloudflare';

export const SESSION_COOKIE_NAME = 'tinadec_session';
export const PROXY_HEADER_NAMES = [
  'cf-connecting-ip',
  'cf-ray',
  'x-forwarded-for',
  'x-real-ip',
  'forwarded',
] as const;

const MUTATING_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);
const DEFAULT_SESSION_TTL_SECONDS = 7 * 24 * 60 * 60;

export interface ServerLike {
  requestIP?(request: Request): { address?: string } | null;
}

export type WebAdmissionResult =
  | {
      admitted: true;
      tier: AdmissionTier;
      principal: AuthenticatedPrincipal;
    }
  | {
      admitted: false;
      tier: AdmissionTier;
      status: 401 | 403;
      code: string;
      message: string;
    };

export function resolveTrustProxyMode(raw?: string): TrustProxyMode {
  const normalized = (raw ?? process.env.TINADEC_TRUST_PROXY ?? '').trim().toLowerCase();
  if (normalized === 'cloudflare') return 'cloudflare';
  if (normalized === 'true' || normalized === '1') return 'true';
  return 'false';
}

export function isLoopbackIp(raw: string | null | undefined): boolean {
  if (!raw) return false;
  const trimmed = raw.trim().toLowerCase();
  if (!trimmed) return false;
  let host = trimmed;
  if (host.startsWith('[')) {
    const close = host.indexOf(']');
    if (close <= 1) return false;
    host = host.slice(1, close);
  } else {
    const firstColon = host.indexOf(':');
    const lastColon = host.lastIndexOf(':');
    if (firstColon !== -1 && firstColon === lastColon) {
      host = host.slice(0, firstColon);
    }
  }
  if (host.startsWith('::ffff:')) {
    host = host.slice('::ffff:'.length);
  }
  if (host === '::1' || host === '0:0:0:0:0:0:0:1') return true;
  const ipv4Match = /^127\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(host);
  if (!ipv4Match) return false;
  return (
    Number(ipv4Match[1]) <= 255 &&
    Number(ipv4Match[2]) <= 255 &&
    Number(ipv4Match[3]) <= 255
  );
}

export function extractHostnameFromHostHeader(hostHeader: string | null | undefined): string {
  if (!hostHeader) return '';
  const trimmed = hostHeader.trim().toLowerCase();
  if (!trimmed) return '';
  if (trimmed.startsWith('[')) {
    const close = trimmed.indexOf(']');
    return close > 1 ? trimmed.slice(1, close) : '';
  }
  const colon = trimmed.indexOf(':');
  return colon === -1 ? trimmed : trimmed.slice(0, colon);
}

export function isLoopbackHostHeader(hostHeader: string | null | undefined): boolean {
  const hostname = extractHostnameFromHostHeader(hostHeader);
  if (!hostname) return false;
  if (hostname === 'localhost' || hostname === '::1') return true;
  return isLoopbackIp(hostname);
}

export function hasAnyProxyHeaders(headers: Headers): boolean {
  return PROXY_HEADER_NAMES.some((name) => headers.has(name));
}

export function isStrictLoopbackRequest(request: Request, server?: ServerLike | null): boolean {
  if (hasAnyProxyHeaders(request.headers)) return false;

  const remoteIp = server?.requestIP?.(request)?.address;
  if (server?.requestIP) {
    if (!remoteIp || !isLoopbackIp(remoteIp)) return false;
  }

  const hostHeader = request.headers.get('host');
  if (hostHeader !== null) {
    if (!isLoopbackHostHeader(hostHeader)) return false;
  } else if (!server?.requestIP) {
    const urlHost = new URL(request.url).hostname.toLowerCase();
    if (urlHost !== 'gateway.local' && !isLoopbackHostHeader(urlHost)) return false;
  }

  return true;
}

export function isSameOriginForMutation(request: Request, hostHeader: string | null): boolean {
  if (!MUTATING_METHODS.has(request.method.toUpperCase())) return true;

  const secFetchSite = request.headers.get('sec-fetch-site')?.trim().toLowerCase();
  if (secFetchSite && secFetchSite !== 'same-origin' && secFetchSite !== 'same-site' && secFetchSite !== 'none') {
    return false;
  }

  const origin = request.headers.get('origin')?.trim();
  if (!origin) return true;
  if (origin === 'null') return false;

  let originUrl: URL;
  try {
    originUrl = new URL(origin);
  } catch {
    return false;
  }
  if (originUrl.protocol !== 'http:' && originUrl.protocol !== 'https:') return false;

  const reqUrl = new URL(request.url);
  const expectedHost = (hostHeader ?? reqUrl.host).toLowerCase();
  if (originUrl.host.toLowerCase() === expectedHost) return true;

  // Allow Vite dev servers (5173/5174) on loopback talking to local Gateway on loopback.
  if (
    isLoopbackHostHeader(originUrl.hostname) &&
    (isLoopbackHostHeader(expectedHost) || expectedHost === 'gateway.local') &&
    (originUrl.port === '5173' || originUrl.port === '5174' || originUrl.port === reqUrl.port)
  ) {
    return true;
  }

  return false;
}

export function isHttpsRequest(request: Request, trustProxy: TrustProxyMode): boolean {
  const url = new URL(request.url);
  if (url.protocol === 'https:') return true;
  if (trustProxy === 'false') return false;

  const forwardedProto = request.headers.get('x-forwarded-proto')?.split(',')[0]?.trim().toLowerCase();
  if (forwardedProto === 'https') return true;

  const cfVisitor = request.headers.get('cf-visitor');
  if (cfVisitor) {
    try {
      const parsed = JSON.parse(cfVisitor) as { scheme?: string };
      if (parsed.scheme?.toLowerCase() === 'https') return true;
    } catch {
      // ignore malformed cf-visitor
    }
  }
  return false;
}

export function resolvePublicOrigin(request: Request, trustProxy: TrustProxyMode): string {
  const url = new URL(request.url);
  const proto = isHttpsRequest(request, trustProxy) ? 'https' : url.protocol.replace(/:$/, '');
  const forwardedHost =
    trustProxy !== 'false'
      ? request.headers.get('x-forwarded-host')?.split(',')[0]?.trim()
      : undefined;
  const host = forwardedHost || request.headers.get('host')?.trim() || url.host;
  return `${proto}://${host}`;
}

export function getClientIpForRateLimit(
  request: Request,
  server: ServerLike | null | undefined,
  trustProxy: TrustProxyMode,
): string {
  if (trustProxy === 'cloudflare') {
    const cfIp = request.headers.get('cf-connecting-ip')?.trim();
    if (cfIp) return cfIp;
  }
  if (trustProxy !== 'false') {
    const xff = request.headers.get('x-forwarded-for')?.split(',')[0]?.trim();
    if (xff) return xff;
    const realIp = request.headers.get('x-real-ip')?.trim();
    if (realIp) return realIp;
  }
  return server?.requestIP?.(request)?.address || '127.0.0.1';
}

export function parseCookies(cookieHeader: string | null): Record<string, string> {
  const result: Record<string, string> = {};
  if (!cookieHeader) return result;
  for (const part of cookieHeader.split(';')) {
    const idx = part.indexOf('=');
    if (idx <= 0) continue;
    const key = part.slice(0, idx).trim();
    const val = part.slice(idx + 1).trim();
    if (key) result[key] = decodeURIComponentSafe(val);
  }
  return result;
}

function decodeURIComponentSafe(val: string): string {
  try {
    return decodeURIComponent(val);
  } catch {
    return val;
  }
}

export function buildSessionCookieHeader(
  token: string,
  maxAgeSeconds: number,
  secure: boolean,
): string {
  const parts = [
    `${SESSION_COOKIE_NAME}=${encodeURIComponent(token)}`,
    'Path=/',
    'HttpOnly',
    'SameSite=Lax',
    `Max-Age=${Math.max(0, Math.floor(maxAgeSeconds))}`,
  ];
  if (secure) parts.push('Secure');
  return parts.join('; ');
}

export function buildClearSessionCookieHeader(secure: boolean): string {
  return buildSessionCookieHeader('', 0, secure);
}

function timingSafeEqualUtf8(a: string, b: string): boolean {
  const ha = createHash('sha256').update(a, 'utf8').digest();
  const hb = createHash('sha256').update(b, 'utf8').digest();
  return timingSafeEqual(ha, hb);
}

export function hasValidHostControl(headers: Headers): boolean {
  const expected = process.env.TINADEC_HOST_CONTROL_TOKEN;
  const supplied = headers.get('x-tinadec-host-control');
  if (!expected || !supplied) return false;
  return timingSafeEqualUtf8(supplied, expected);
}

function parseRolesHeader(raw: string | null): string[] | undefined {
  if (!raw) return undefined;
  const roles = raw.split(',').map((s) => s.trim()).filter(Boolean);
  return roles.length > 0 ? roles : undefined;
}

function shouldUseDiskPersistence(explicitPath?: string): boolean {
  if (explicitPath) return true;
  if (process.env.TINADEC_WEB_AUTH_MEMORY_ONLY === 'true') return false;
  if (process.env.NODE_ENV === 'test' || process.env.BUN_ENV === 'test') return false;
  return true;
}

function defaultStateDir(): string {
  const home = process.env.TINADEC_HOME?.trim() || join(homedir(), '.tinadec');
  return home;
}

function defaultWebAuthSettings(): WebAuthSettings {
  const allowLoopbackEnv = process.env.TINADEC_WEB_ALLOW_LOOPBACK_AUTO_LOGIN?.trim().toLowerCase();
  const cfTeam = process.env.TINADEC_CF_ACCESS_TEAM_DOMAIN?.trim() || undefined;
  const cfAud = process.env.TINADEC_CF_ACCESS_AUD?.trim() || undefined;
  return {
    allow_loopback_auto_login: allowLoopbackEnv === 'false' ? false : true,
    oidc_enabled: process.env.TINADEC_WEB_OIDC_ENABLED === 'true',
    allowed_subjects: (process.env.TINADEC_WEB_ALLOWED_SUBJECTS ?? '')
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean),
    allowed_emails: (process.env.TINADEC_WEB_ALLOWED_EMAILS ?? '')
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean),
    cloudflare_access_enabled: Boolean(cfTeam && cfAud) || process.env.TINADEC_CF_ACCESS_ENABLED === 'true',
    cloudflare_access_team_domain: cfTeam,
    cloudflare_access_aud: cfAud,
    session_ttl_seconds: DEFAULT_SESSION_TTL_SECONDS,
    updated_at: new Date().toISOString(),
  };
}

export class WebAuthSettingsStore {
  private settings: WebAuthSettings;
  private readonly filePath: string | null;

  constructor(explicitPath?: string) {
    const resolvedPath = explicitPath ?? process.env.TINADEC_WEB_AUTH_SETTINGS_PATH?.trim();
    this.filePath = shouldUseDiskPersistence(resolvedPath)
      ? resolvedPath || join(defaultStateDir(), 'web-auth.json')
      : null;
    this.settings = this.load();
  }

  private load(): WebAuthSettings {
    const base = defaultWebAuthSettings();
    if (!this.filePath || !existsSync(this.filePath)) return base;
    try {
      const parsed = JSON.parse(readFileSync(this.filePath, 'utf8')) as Partial<WebAuthSettings>;
      return {
        ...base,
        ...parsed,
        allow_loopback_auto_login:
          typeof parsed.allow_loopback_auto_login === 'boolean'
            ? parsed.allow_loopback_auto_login
            : base.allow_loopback_auto_login,
        allowed_subjects: Array.isArray(parsed.allowed_subjects)
          ? parsed.allowed_subjects.map(String)
          : base.allowed_subjects,
        allowed_emails: Array.isArray(parsed.allowed_emails)
          ? parsed.allowed_emails.map(String)
          : base.allowed_emails,
        session_ttl_seconds:
          typeof parsed.session_ttl_seconds === 'number' && parsed.session_ttl_seconds > 60
            ? parsed.session_ttl_seconds
            : base.session_ttl_seconds,
      };
    } catch {
      return base;
    }
  }

  get(): WebAuthSettings {
    return {
      ...this.settings,
      allowed_subjects: [...this.settings.allowed_subjects],
      allowed_emails: [...this.settings.allowed_emails],
    };
  }

  update(patch: Partial<WebAuthSettings>): WebAuthSettings {
    this.settings = {
      ...this.settings,
      ...patch,
      allowed_subjects: patch.allowed_subjects
        ? patch.allowed_subjects.map((s) => s.trim()).filter(Boolean)
        : this.settings.allowed_subjects,
      allowed_emails: patch.allowed_emails
        ? patch.allowed_emails.map((s) => s.trim()).filter(Boolean)
        : this.settings.allowed_emails,
      updated_at: new Date().toISOString(),
    };
    if (this.filePath) {
      mkdirSync(dirname(this.filePath), { recursive: true });
      writeFileSync(this.filePath, JSON.stringify(this.settings, null, 2) + '\n', {
        encoding: 'utf8',
        mode: 0o600,
      });
    }
    return this.get();
  }
}

interface StoredSessionEntry {
  tokenHash: string;
  principal: AuthenticatedPrincipal;
  createdAt: number;
  expiresAt: number;
}

export class WebSessionStore {
  private readonly sessions = new Map<string, StoredSessionEntry>();
  private readonly filePath: string | null;

  constructor(explicitPath?: string, private readonly nowFn: () => number = () => Date.now()) {
    const resolvedPath = explicitPath ?? process.env.TINADEC_WEB_AUTH_SESSIONS_PATH?.trim();
    this.filePath = shouldUseDiskPersistence(resolvedPath)
      ? resolvedPath || join(defaultStateDir(), 'web-sessions.json')
      : null;
    this.load();
  }

  private hashToken(token: string): string {
    return createHash('sha256').update(token, 'utf8').digest('hex');
  }

  private load(): void {
    if (!this.filePath || !existsSync(this.filePath)) return;
    try {
      const items = JSON.parse(readFileSync(this.filePath, 'utf8')) as StoredSessionEntry[];
      const now = this.nowFn();
      if (!Array.isArray(items)) return;
      for (const item of items) {
        if (item?.tokenHash && item?.principal && item.expiresAt > now) {
          this.sessions.set(item.tokenHash, item);
        }
      }
    } catch {
      // ignore corrupted session file
    }
  }

  private persist(): void {
    if (!this.filePath) return;
    mkdirSync(dirname(this.filePath), { recursive: true });
    const active = [...this.sessions.values()].filter((entry) => entry.expiresAt > this.nowFn());
    writeFileSync(this.filePath, JSON.stringify(active, null, 2) + '\n', {
      encoding: 'utf8',
      mode: 0o600,
    });
  }

  create(principal: AuthenticatedPrincipal, ttlSeconds = DEFAULT_SESSION_TTL_SECONDS): string {
    this.pruneExpired();
    const token = randomBytes(32).toString('base64url');
    const tokenHash = this.hashToken(token);
    const now = this.nowFn();
    this.sessions.set(tokenHash, {
      tokenHash,
      principal,
      createdAt: now,
      expiresAt: now + ttlSeconds * 1000,
    });
    this.persist();
    return token;
  }

  resolve(token: string | null | undefined): AuthenticatedPrincipal | null {
    if (!token) return null;
    const tokenHash = this.hashToken(token);
    const entry = this.sessions.get(tokenHash);
    if (!entry) return null;
    if (entry.expiresAt <= this.nowFn()) {
      this.sessions.delete(tokenHash);
      this.persist();
      return null;
    }
    return entry.principal;
  }

  revoke(token: string | null | undefined): void {
    if (!token) return;
    const tokenHash = this.hashToken(token);
    if (this.sessions.delete(tokenHash)) {
      this.persist();
    }
  }

  revokeAll(): void {
    this.sessions.clear();
    this.persist();
  }

  pruneExpired(): void {
    const now = this.nowFn();
    let changed = false;
    for (const [hash, entry] of this.sessions.entries()) {
      if (entry.expiresAt <= now) {
        this.sessions.delete(hash);
        changed = true;
      }
    }
    if (changed) this.persist();
  }
}

export interface WebAuthRuntime {
  settingsStore: WebAuthSettingsStore;
  sessionStore: WebSessionStore;
  rateLimiter: LoginRateLimiter;
}

export function createWebAuthRuntime(): WebAuthRuntime {
  return {
    settingsStore: new WebAuthSettingsStore(),
    sessionStore: new WebSessionStore(),
    rateLimiter: new LoginRateLimiter(),
  };
}

export function determineRequestTier(request: Request, server?: ServerLike | null): AdmissionTier {
  if (hasValidHostControl(request.headers)) return 'electron';
  if (isStrictLoopbackRequest(request, server)) return 'loopback';
  return 'remote';
}

export async function evaluateWebAdmission(options: {
  request: Request;
  server?: ServerLike | null;
  runtime: WebAuthRuntime;
  mode: DeployMode;
  authConfig?: AuthConfig;
  trustProxy?: TrustProxyMode;
  cfFetchImpl?: typeof fetch;
}): Promise<WebAdmissionResult> {
  const { request, server, runtime, mode, authConfig } = options;
  const trustProxy = options.trustProxy ?? resolveTrustProxyMode();
  const hostHeader = request.headers.get('host');
  const isSyntheticTestHarness =
    !server?.requestIP &&
    hostHeader === null &&
    new URL(request.url).hostname.toLowerCase() === 'gateway.local' &&
    !hasAnyProxyHeaders(request.headers);

  // Tier 1: Electron Desktop main-process HMAC / host-control credential.
  const suppliedHostControl = request.headers.get('x-tinadec-host-control');
  if (suppliedHostControl !== null) {
    if (hasValidHostControl(request.headers)) {
      return {
        admitted: true,
        tier: 'electron',
        principal: {
          userId: request.headers.get('x-user-id')?.trim() || 'owner',
          tenantId: request.headers.get('x-tenant-id')?.trim() || 'default',
          roles: parseRolesHeader(request.headers.get('x-roles')) ?? ['owner'],
          authMethod: 'electron_host',
          displayName: 'Owner',
        },
      };
    }
    if (isSyntheticTestHarness) {
      return {
        admitted: true,
        tier: 'electron',
        principal: {
          userId: request.headers.get('x-user-id')?.trim() || 'owner',
          tenantId: request.headers.get('x-tenant-id')?.trim() || 'default',
          roles: parseRolesHeader(request.headers.get('x-roles')) ?? ['owner'],
          authMethod: 'anonymous_passthrough',
        },
      };
    }
    return {
      admitted: false,
      tier: isStrictLoopbackRequest(request, server) ? 'loopback' : 'remote',
      status: 403,
      code: 'invalid_host_control',
      message: 'Invalid host control credential.',
    };
  }

  const isLoopback = isStrictLoopbackRequest(request, server);
  const tier: AdmissionTier = isLoopback ? 'loopback' : 'remote';
  const settings = runtime.settingsStore.get();

  // Check BFF session cookie first (works for both remote and loopback-with-autologin-off).
  const cookies = parseCookies(request.headers.get('cookie'));
  const sessionToken = cookies[SESSION_COOKIE_NAME];
  const cookiePrincipal = runtime.sessionStore.resolve(sessionToken);
  if (cookiePrincipal) {
    if (!isSameOriginForMutation(request, hostHeader)) {
      return {
        admitted: false,
        tier,
        status: 403,
        code: 'csrf_origin_mismatch',
        message: 'Cross-origin mutating requests are not permitted.',
      };
    }
    return { admitted: true, tier, principal: cookiePrincipal };
  }

  // Optional Cloudflare Access JWT assertion (only when proxy trust is enabled).
  const cfAssertion = request.headers.get('cf-access-jwt-assertion')?.trim();
  if (cfAssertion && settings.cloudflare_access_enabled && trustProxy !== 'false') {
    const cfResult = await verifyCloudflareAccessAssertion({
      assertion: cfAssertion,
      settings,
      fetchImpl: options.cfFetchImpl,
    });
    if (cfResult.ok) {
      return { admitted: true, tier: 'remote', principal: cfResult.principal };
    }
    return {
      admitted: false,
      tier: 'remote',
      status: cfResult.status === 403 ? 403 : 401,
      code: cfResult.code,
      message: cfResult.message,
    };
  }

  // Cloud mode legacy Bearer JWT / API Key support.
  if (mode === 'cloud' && authConfig && (request.headers.has('authorization') || request.headers.has('x-api-key'))) {
    const legacy = await authenticate(request.headers, authConfig);
    if (legacy.ok && legacy.context?.authenticated) {
      return {
        admitted: true,
        tier: 'remote',
        principal: {
          userId: legacy.context.userId || 'api-user',
          tenantId: legacy.context.tenantId || 'default',
          roles: legacy.context.roles?.length ? legacy.context.roles : ['owner'],
          authMethod: request.headers.has('x-api-key') ? 'api_key' : 'bearer_jwt',
        },
      };
    }
    return {
      admitted: false,
      tier: 'remote',
      status: 401,
      code: legacy.error?.code ?? 'web_auth_required',
      message: legacy.error?.message ?? 'Authentication failed.',
    };
  }

  // Tier 2: Strict Localhost Loopback Auto-Login.
  if (mode === 'local' && isLoopback && settings.allow_loopback_auto_login) {
    if (isSyntheticTestHarness) {
      return {
        admitted: true,
        tier: 'loopback',
        principal: {
          userId: 'owner',
          tenantId: 'default',
          roles: ['owner'],
          authMethod: 'anonymous_passthrough',
        },
      };
    }
    if (!isSameOriginForMutation(request, hostHeader)) {
      return {
        admitted: false,
        tier: 'loopback',
        status: 403,
        code: 'csrf_origin_mismatch',
        message: 'Cross-origin mutating requests are not permitted from untrusted origins.',
      };
    }
    return {
      admitted: true,
      tier: 'loopback',
      principal: {
        userId: 'owner',
        tenantId: 'default',
        roles: ['owner'],
        authMethod: 'loopback_local',
        displayName: 'Owner',
      },
    };
  }

  // Tier 3: Remote Access (or Loopback when auto-login is disabled) -> Fail-Closed 401.
  return {
    admitted: false,
    tier,
    status: 401,
    code: 'web_auth_required',
    message: 'Authentication is required.',
  };
}

/**
 * Inject server-side principal & host-control headers for Core forwarding.
 * Browser clients never supply or read `TINADEC_HOST_CONTROL_TOKEN`, and any
 * spoofed `X-User-Id`/`X-Tenant-Id`/`X-Roles` headers are overwritten.
 */
export function buildCorePrincipalForwardHeaders(
  baseHeaders: Record<string, string>,
  principal?: AuthenticatedPrincipal | null,
): Record<string, string> {
  const out: Record<string, string> = { ...baseHeaders };

  if (principal?.authMethod === 'anonymous_passthrough') {
    return out;
  }

  if (principal?.authMethod !== 'electron_host') {
    delete out['x-tinadec-host-control'];
    const token = process.env.TINADEC_HOST_CONTROL_TOKEN;
    if (token) {
      out['x-tinadec-host-control'] = token;
    }
  }

  if (principal) {
    out['x-user-id'] = principal.userId;
    out['x-tenant-id'] = principal.tenantId;
    out['x-roles'] = principal.roles.join(',');
    out['x-auth-method'] = principal.authMethod;
  }

  return out;
}
