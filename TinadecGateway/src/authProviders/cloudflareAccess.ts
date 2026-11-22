import type { AuthVerificationResult, WebAuthSettings } from './types.js';

interface CfJwk extends JsonWebKey {
  kid?: string;
  alg?: string;
  use?: string;
}

interface JwksCacheEntry {
  keys: CfJwk[];
  fetchedAt: number;
  lastForcedRefresh: number;
}

const JWKS_TTL_MS = 10 * 60 * 1000;
const FORCED_REFRESH_MIN_INTERVAL_MS = 30 * 1000;
const jwksCache = new Map<string, JwksCacheEntry>();

export function clearCloudflareJwksCache(): void {
  jwksCache.clear();
}

function base64urlToBytes(segment: string): Uint8Array {
  let base64 = segment.replace(/-/g, '+').replace(/_/g, '/');
  const pad = base64.length % 4;
  if (pad === 2) base64 += '==';
  else if (pad === 3) base64 += '=';
  const raw = atob(base64);
  const out = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
  return out;
}

function decodeJsonSegment<T>(segment: string): T | null {
  try {
    const text = new TextDecoder().decode(base64urlToBytes(segment));
    return JSON.parse(text) as T;
  } catch {
    return null;
  }
}

export function normalizeTeamDomain(raw?: string): string {
  const trimmed = (raw ?? '').trim().replace(/^https?:\/\//i, '').replace(/\/+$/, '');
  return trimmed;
}

function audienceMatches(aud: unknown, expectedAud: string): boolean {
  if (typeof aud === 'string') return aud === expectedAud;
  if (Array.isArray(aud)) return aud.some((item) => item === expectedAud);
  return false;
}

async function fetchJwks(
  jwksUrl: string,
  forceRefresh: boolean,
  fetchImpl: typeof fetch,
  nowMs: number,
): Promise<CfJwk[]> {
  const existing = jwksCache.get(jwksUrl);
  if (existing) {
    if (!forceRefresh && nowMs - existing.fetchedAt < JWKS_TTL_MS) return existing.keys;
    if (forceRefresh && nowMs - existing.lastForcedRefresh < FORCED_REFRESH_MIN_INTERVAL_MS) return existing.keys;
  }

  const response = await fetchImpl(jwksUrl, { headers: { accept: 'application/json' } });
  if (!response.ok) {
    throw new Error(`Failed to fetch Cloudflare Access JWKS (${response.status})`);
  }
  const body = (await response.json()) as { keys?: CfJwk[] };
  const keys = Array.isArray(body.keys) ? body.keys : [];
  jwksCache.set(jwksUrl, {
    keys,
    fetchedAt: nowMs,
    lastForcedRefresh: forceRefresh ? nowMs : existing?.lastForcedRefresh ?? 0,
  });
  return keys;
}

async function verifySignature(
  signingInput: string,
  signatureBytes: Uint8Array,
  alg: string,
  jwk: CfJwk,
): Promise<boolean> {
  const inputBytes = new TextEncoder().encode(signingInput);
  if (alg === 'RS256' && jwk.kty === 'RSA') {
    const key = await crypto.subtle.importKey(
      'jwk',
      jwk,
      { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' },
      false,
      ['verify'],
    );
    return crypto.subtle.verify(
      'RSASSA-PKCS1-v1_5',
      key,
      signatureBytes as unknown as BufferSource,
      inputBytes as unknown as BufferSource,
    );
  }
  if (alg === 'ES256' && jwk.kty === 'EC') {
    const key = await crypto.subtle.importKey(
      'jwk',
      jwk,
      { name: 'ECDSA', namedCurve: 'P-256' },
      false,
      ['verify'],
    );
    return crypto.subtle.verify(
      { name: 'ECDSA', hash: 'SHA-256' },
      key,
      signatureBytes as unknown as BufferSource,
      inputBytes as unknown as BufferSource,
    );
  }
  return false;
}

export async function verifyCloudflareAccessAssertion(options: {
  assertion: string;
  settings: WebAuthSettings;
  fetchImpl?: typeof fetch;
  nowSeconds?: number;
}): Promise<AuthVerificationResult> {
  const { assertion, settings } = options;
  const fetchImpl = options.fetchImpl ?? fetch;
  const nowSec = options.nowSeconds ?? Math.floor(Date.now() / 1000);

  const teamDomain = normalizeTeamDomain(
    settings.cloudflare_access_team_domain || process.env.TINADEC_CF_ACCESS_TEAM_DOMAIN,
  );
  const expectedAud = (settings.cloudflare_access_aud || process.env.TINADEC_CF_ACCESS_AUD || '').trim();
  if (!teamDomain || !expectedAud) {
    return {
      ok: false,
      status: 401,
      code: 'cloudflare_access_not_configured',
      message: 'Cloudflare Access team domain or audience is not configured.',
    };
  }

  const parts = assertion.trim().split('.');
  if (parts.length !== 3) {
    return { ok: false, status: 401, code: 'invalid_cf_access_jwt', message: 'Malformed Cloudflare Access JWT.' };
  }

  const header = decodeJsonSegment<{ alg?: string; kid?: string }>(parts[0]!);
  const payload = decodeJsonSegment<{
    iss?: string;
    aud?: string | string[];
    sub?: string;
    email?: string;
    exp?: number;
    nbf?: number;
  }>(parts[1]!);

  if (!header || !payload || !header.kid || (header.alg !== 'RS256' && header.alg !== 'ES256')) {
    return { ok: false, status: 401, code: 'invalid_cf_access_jwt', message: 'Unsupported Cloudflare Access JWT header.' };
  }

  const expectedIssuer = `https://${teamDomain}`;
  const actualIssuer = (payload.iss ?? '').trim().replace(/\/+$/, '');
  if (actualIssuer.toLowerCase() !== expectedIssuer.toLowerCase()) {
    return { ok: false, status: 401, code: 'invalid_cf_access_issuer', message: 'Cloudflare Access issuer mismatch.' };
  }

  if (!audienceMatches(payload.aud, expectedAud)) {
    return { ok: false, status: 401, code: 'invalid_cf_access_audience', message: 'Cloudflare Access audience mismatch.' };
  }

  if (typeof payload.exp !== 'number' || payload.exp <= nowSec) {
    return { ok: false, status: 401, code: 'cf_access_token_expired', message: 'Cloudflare Access token expired.' };
  }

  if (typeof payload.nbf === 'number' && payload.nbf > nowSec + 60) {
    return { ok: false, status: 401, code: 'cf_access_token_not_active', message: 'Cloudflare Access token is not yet valid.' };
  }

  const jwksUrl = `${expectedIssuer}/cdn-cgi/access/certs`;
  let keys: CfJwk[];
  try {
    keys = await fetchJwks(jwksUrl, false, fetchImpl, nowSec * 1000);
    if (!keys.some((k) => k.kid === header.kid)) {
      keys = await fetchJwks(jwksUrl, true, fetchImpl, nowSec * 1000);
    }
  } catch {
    return { ok: false, status: 401, code: 'cf_access_jwks_unavailable', message: 'Unable to validate Cloudflare Access keys.' };
  }

  const key = keys.find((k) => k.kid === header.kid);
  if (!key) {
    return { ok: false, status: 401, code: 'cf_access_unknown_key', message: 'Unknown Cloudflare Access signing key.' };
  }

  let signatureValid = false;
  try {
    const signatureBytes = base64urlToBytes(parts[2]!);
    signatureValid = await verifySignature(`${parts[0]}.${parts[1]}`, signatureBytes, header.alg, key);
  } catch {
    signatureValid = false;
  }

  if (!signatureValid) {
    return { ok: false, status: 401, code: 'invalid_cf_access_signature', message: 'Invalid Cloudflare Access signature.' };
  }

  const email = payload.email?.trim();
  const sub = payload.sub?.trim();
  if (!email && !sub) {
    return { ok: false, status: 401, code: 'cf_access_missing_subject', message: 'Cloudflare Access token lacks identity claims.' };
  }

  if (settings.allowed_emails.length > 0) {
    const emailLower = email?.toLowerCase();
    const allowed = Boolean(emailLower && settings.allowed_emails.some((item) => item.trim().toLowerCase() === emailLower));
    if (!allowed) {
      return { ok: false, status: 403, code: 'cf_access_email_forbidden', message: 'Your Cloudflare Access identity is not authorized for this instance.' };
    }
  }

  return {
    ok: true,
    principal: {
      userId: email || sub!,
      tenantId: 'default',
      roles: ['owner'],
      authMethod: 'cloudflare_access',
      displayName: email || sub,
      email,
    },
  };
}
