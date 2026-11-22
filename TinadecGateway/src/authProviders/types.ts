export type WebAuthMethod =
  | 'electron_host'
  | 'loopback_local'
  | 'local_password'
  | 'official_oidc'
  | 'cloudflare_access'
  | 'bearer_jwt'
  | 'api_key'
  | 'anonymous_passthrough';

export type AuthProviderId = 'local_password' | 'official_oidc' | 'cloudflare_access';

export interface AuthenticatedPrincipal {
  userId: string;
  tenantId: string;
  roles: string[];
  authMethod: WebAuthMethod;
  displayName?: string;
  email?: string;
  avatarUrl?: string;
}

export interface WebAuthSettings {
  allow_loopback_auto_login: boolean;
  password_hash?: string;
  oidc_enabled: boolean;
  allowed_subjects: string[];
  allowed_emails: string[];
  cloudflare_access_enabled: boolean;
  cloudflare_access_team_domain?: string;
  cloudflare_access_aud?: string;
  session_ttl_seconds: number;
  updated_at: string;
}

export type AuthVerificationResult =
  | { ok: true; principal: AuthenticatedPrincipal }
  | { ok: false; status: 401 | 403 | 429 | 502 | 503; code: string; message: string; retryAfterSeconds?: number };
