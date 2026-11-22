import type { AuthenticatedPrincipal, AuthVerificationResult, WebAuthSettings } from './types.js';

export interface OidcIdentityResult {
  subject: string;
  nickname?: string;
  email?: string;
  avatar_url?: string;
}

interface PendingOidcLogin {
  state: string;
  redirectUri: string;
  returnTo: string;
  createdAt: number;
}

const LOGIN_TTL_MS = 10 * 60 * 1000;
const MAX_PENDING = 256;

export class WebOidcBridge {
  private readonly pending = new Map<string, PendingOidcLogin>();

  pruneExpired(now = Date.now()): void {
    for (const [key, entry] of this.pending.entries()) {
      if (now - entry.createdAt > LOGIN_TTL_MS) {
        this.pending.delete(key);
      }
    }
  }

  recordPending(state: string, redirectUri: string, returnTo = '/'): void {
    this.pruneExpired();
    if (this.pending.size >= MAX_PENDING) {
      const oldest = this.pending.keys().next().value;
      if (oldest) this.pending.delete(oldest);
    }
    this.pending.set(state, {
      state,
      redirectUri,
      returnTo: sanitizeReturnTo(returnTo),
      createdAt: Date.now(),
    });
  }

  consumePending(state: string): PendingOidcLogin | null {
    this.pruneExpired();
    const entry = this.pending.get(state) ?? null;
    if (entry) this.pending.delete(state);
    return entry;
  }

  clear(): void {
    this.pending.clear();
  }
}

export function sanitizeReturnTo(raw: string | null | undefined): string {
  if (!raw) return '/';
  const trimmed = raw.trim();
  if (!trimmed.startsWith('/') || trimmed.startsWith('//') || /[\r\n]/.test(trimmed)) return '/';
  return trimmed;
}

export function isOidcIdentityAllowed(identity: OidcIdentityResult, settings: WebAuthSettings): boolean {
  const subjects = settings.allowed_subjects.map((s) => s.trim()).filter(Boolean);
  const emails = settings.allowed_emails.map((e) => e.trim().toLowerCase()).filter(Boolean);

  if (subjects.length > 0 || emails.length > 0) {
    const subjectMatch = subjects.includes(identity.subject);
    const emailMatch = Boolean(identity.email && emails.includes(identity.email.trim().toLowerCase()));
    return subjectMatch || emailMatch;
  }

  return process.env.TINADEC_WEB_OIDC_ALLOW_OPEN_SIGNIN === 'true';
}

export async function beginCoreOidcLogin(options: {
  coreUrl: string;
  redirectUri: string;
  settings: WebAuthSettings;
  hostControlToken?: string;
  fetchImpl?: typeof fetch;
}): Promise<{ ok: true; authorizeUrl: string; state: string } | { ok: false; status: number; code: string; message: string }> {
  const fetchImpl = options.fetchImpl ?? fetch;
  const headers: Record<string, string> = { accept: 'application/json' };
  if (options.hostControlToken) headers['x-tinadec-host-control'] = options.hostControlToken;

  const beginUrl = new URL('/auth/begin', options.coreUrl);
  beginUrl.searchParams.set('redirect_uri', options.redirectUri);
  beginUrl.searchParams.set('mode', 'web');
  if (options.settings.allowed_subjects.length > 0) {
    beginUrl.searchParams.set('allowed_subjects', options.settings.allowed_subjects.join(','));
  }
  if (options.settings.allowed_emails.length > 0) {
    beginUrl.searchParams.set('allowed_emails', options.settings.allowed_emails.join(','));
  }

  let response: Response;
  try {
    response = await fetchImpl(beginUrl.toString(), { headers, redirect: 'manual' });
  } catch (error) {
    return {
      ok: false,
      status: 502,
      code: 'core_unreachable',
      message: error instanceof Error ? error.message : 'Cannot reach Core OIDC service.',
    };
  }

  const data = (await response.json().catch(() => ({}))) as {
    authorizeUrl?: string;
    authorize_url?: string;
    state?: string;
    code?: string;
    message?: string;
  };
  if (!response.ok) {
    return {
      ok: false,
      status: response.status,
      code: data.code || 'oidc_begin_failed',
      message: data.message || 'OIDC login could not be initiated.',
    };
  }

  const authorizeUrl = data.authorizeUrl ?? data.authorize_url;
  if (!authorizeUrl || !data.state) {
    return { ok: false, status: 502, code: 'oidc_invalid_response', message: 'Core returned incomplete OIDC authorization metadata.' };
  }

  return { ok: true, authorizeUrl, state: data.state };
}

export async function completeCoreOidcCallback(options: {
  coreUrl: string;
  query: URLSearchParams;
  settings: WebAuthSettings;
  redirectUri?: string;
  hostControlToken?: string;
  fetchImpl?: typeof fetch;
}): Promise<AuthVerificationResult> {
  const fetchImpl = options.fetchImpl ?? fetch;
  const headers: Record<string, string> = { accept: 'application/json' };
  if (options.hostControlToken) headers['x-tinadec-host-control'] = options.hostControlToken;

  const state = options.query.get('state')?.trim() ?? '';
  const code = options.query.get('code')?.trim() ?? '';
  if (!state) {
    return { ok: false, status: 401, code: 'invalid_oidc_callback', message: 'Missing OIDC state parameter.' };
  }

  // Prefer Core's host-gated POST /auth/exchange when code is present so Gateway owns the callback cleanly.
  if (code) {
    const exchangeUrl = new URL('/auth/exchange', options.coreUrl);
    let exchangeResp: Response;
    try {
      exchangeResp = await fetchImpl(exchangeUrl.toString(), {
        method: 'POST',
        headers: { ...headers, 'content-type': 'application/json' },
        body: JSON.stringify({
          code,
          state,
          ...(options.redirectUri ? { redirect_uri: options.redirectUri } : {}),
          ...(options.settings.allowed_subjects.length > 0 ? { allowed_subjects: options.settings.allowed_subjects } : {}),
          ...(options.settings.allowed_emails.length > 0 ? { allowed_emails: options.settings.allowed_emails } : {}),
        }),
      });
    } catch (error) {
      return {
        ok: false,
        status: 502,
        code: 'core_unreachable',
        message: error instanceof Error ? error.message : 'Core OIDC exchange failed.',
      };
    }

    if (exchangeResp.ok) {
      const payload = (await exchangeResp.json().catch(() => null)) as {
        identity?: OidcIdentityResult;
      } | null;
      const subject = payload?.identity?.subject?.trim();
      if (subject && payload?.identity) {
        if (!isOidcIdentityAllowed(payload.identity, options.settings)) {
          return {
            ok: false,
            status: 403,
            code: 'oidc_subject_forbidden',
            message: 'This OIDC account is not on the allowed owner list for this TinadecOffice instance.',
          };
        }
        return {
          ok: true,
          principal: {
            userId: subject,
            tenantId: 'default',
            roles: ['owner'],
            authMethod: 'official_oidc',
            displayName: payload.identity.nickname || payload.identity.email || subject,
            email: payload.identity.email,
            avatarUrl: payload.identity.avatar_url,
          },
        };
      }
    } else if (exchangeResp.status !== 404) {
      const errData = (await exchangeResp.json().catch(() => ({}))) as { code?: string; message?: string };
      return {
        ok: false,
        status: exchangeResp.status === 403 ? 403 : 401,
        code: errData.code || 'oidc_exchange_failed',
        message: errData.message || 'OIDC authorization code exchange failed.',
      };
    }
  }

  // Fallback / mode=web redirect check: poll Core /auth/check?state=...
  const checkUrl = new URL('/auth/check', options.coreUrl);
  checkUrl.searchParams.set('state', state);
  const checkResp = await fetchImpl(checkUrl.toString(), { headers });
  if (!checkResp.ok) {
    return { ok: false, status: 401, code: 'oidc_check_failed', message: 'Failed to check OIDC login status.' };
  }
  const checkData = (await checkResp.json().catch(() => ({}))) as {
    status?: string;
    session?: string;
    identity?: OidcIdentityResult;
  };
  if (checkData.status !== 'complete' || !checkData.session) {
    return { ok: false, status: 401, code: 'oidc_login_incomplete', message: 'OIDC login has not completed.' };
  }

  let identity = checkData.identity;
  if (!identity?.subject) {
    const meUrl = new URL('/auth/me', options.coreUrl);
    const meResp = await fetchImpl(meUrl.toString(), {
      headers: {
        ...headers,
        'x-tinadec-session': checkData.session,
      },
    });
    if (!meResp.ok) {
      return { ok: false, status: 401, code: 'oidc_identity_unavailable', message: 'Could not read OIDC identity.' };
    }
    const me = (await meResp.json().catch(() => null)) as {
      subject?: string;
      Subject?: string;
      nickname?: string;
      Nickname?: string;
      email?: string;
      Email?: string;
      avatarUrl?: string;
      avatar_url?: string;
      AvatarUrl?: string;
    } | null;
    const subject = (me?.subject ?? me?.Subject ?? '').trim();
    if (!subject) {
      return { ok: false, status: 401, code: 'oidc_missing_subject', message: 'OIDC identity missing subject.' };
    }
    identity = {
      subject,
      nickname: me?.nickname ?? me?.Nickname,
      email: me?.email ?? me?.Email,
      avatar_url: me?.avatar_url ?? me?.avatarUrl ?? me?.AvatarUrl,
    };
  }

  if (!isOidcIdentityAllowed(identity, options.settings)) {
    return {
      ok: false,
      status: 403,
      code: 'oidc_subject_forbidden',
      message: 'This OIDC account is not on the allowed owner list for this TinadecOffice instance.',
    };
  }

  return {
    ok: true,
    principal: {
      userId: identity.subject,
      tenantId: 'default',
      roles: ['owner'],
      authMethod: 'official_oidc',
      displayName: identity.nickname || identity.email || identity.subject,
      email: identity.email,
      avatarUrl: identity.avatar_url,
    },
  };
}
