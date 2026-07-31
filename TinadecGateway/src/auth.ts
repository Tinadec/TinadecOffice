/**
 * 认证与租户上下文中间件。
 *
 * 本地模式：跳过认证，不提取租户上下文。
 * 云端模式：
 *   - 从 Authorization 头提取 Bearer token 或 API Key
 *   - 验证 JWT (HS256 WebCrypto) 或 API Key
 *   - 从 JWT claims 或自定义头提取租户 ID (X-Tenant-Id)
 *   - 将租户上下文注入到代理请求头中
 *
 * 认证后的请求会将 tenant_id 和 user_id 添加到转发给 Core / Tool Runtime 的头中。
 */

import { getConfig, type AuthConfig } from './config.js';

export interface AuthContext {
  authenticated: boolean;
  userId?: string;
  tenantId?: string;
  roles?: string[];
}

export interface AuthResult {
  ok: boolean;
  context?: AuthContext;
  error?: { code: string; message: string };
}

/** 不需要认证的公共路径 */
const PUBLIC_PATHS = new Set([
  '/api/v1/health',
  '/docs',
  '/docs/',
  '/docs/json',
]);

export function isPublicPath(path: string): boolean {
  return PUBLIC_PATHS.has(path) || path.startsWith('/docs/');
}

/**
 * 验证请求的认证信息，返回认证上下文。
 * 在本地模式下始终返回 authenticated: true。
 * 云端模式下使用 WebCrypto 验证 JWT HS256 签名。
 */
export async function authenticate(
  headers: Headers,
  authConfig?: AuthConfig,
): Promise<AuthResult> {
  // 本地模式：跳过认证
  if (!authConfig) {
    return { ok: true, context: { authenticated: true } };
  }

  const authHeader = headers.get('authorization') ?? '';
  const apiKey = headers.get('x-api-key') ?? '';

  // 尝试 API Key 认证
  if (apiKey && authConfig.apiKeyValidator) {
    if (authConfig.apiKeyValidator(apiKey)) {
      return {
        ok: true,
        context: {
          authenticated: true,
          tenantId: headers.get('x-tenant-id') ?? undefined,
        },
      };
    }
    return {
      ok: false,
      error: { code: 'AUTH_INVALID_API_KEY', message: 'Invalid API key.' },
    };
  }

  // 尝试 Bearer token 认证
  if (authHeader.startsWith('Bearer ')) {
    const token = authHeader.slice(7).trim();
    const decoded = await verifyJwt(token, authConfig.jwtSecret);
    if (decoded) {
      return {
        ok: true,
        context: {
          authenticated: true,
          userId: decoded.sub ?? decoded.user_id,
          tenantId: decoded.tenant_id ?? headers.get('x-tenant-id') ?? undefined,
          roles: decoded.roles,
        },
      };
    }
    return {
      ok: false,
      error: { code: 'AUTH_INVALID_TOKEN', message: 'Invalid or expired token.' },
    };
  }

  // 云端模式要求认证
  if (authConfig.required) {
    return {
      ok: false,
      error: { code: 'AUTH_REQUIRED', message: 'Authentication is required.' },
    };
  }

  // 云端模式但非必须认证：允许匿名访问
  return {
    ok: true,
    context: {
      authenticated: false,
      tenantId: headers.get('x-tenant-id') ?? undefined,
    },
  };
}

interface JwtHeader {
  alg: string;
  typ?: string;
}

interface JwtPayload {
  sub?: string;
  user_id?: string;
  tenant_id?: string;
  roles?: string[];
  exp?: number;
  nbf?: number;
}

/**
 * 验证 JWT (HS256) 签名和声明。
 * 使用 Bun 原生 WebCrypto API 进行 HMAC-SHA256 签名验证。
 * 无密钥时拒绝 Bearer token（云端模式 fail-closed）。
 */
async function verifyJwt(token: string, secret?: string): Promise<JwtPayload | null> {
  const parts = token.split('.');
  if (parts.length !== 3) return null;

  // 解析并验证 header：必须是 HS256
  const header = parseJwtHeader(parts[0]!);
  if (!header || header.alg !== 'HS256') return null;

  try {
    const payload = decodeJwtPayload(parts[1]!);
    if (!payload) return null;

    // 检查过期时间
    if (payload.exp && payload.exp < Math.floor(Date.now() / 1000)) {
      return null;
    }

    // 检查生效时间
    if (payload.nbf && payload.nbf > Math.floor(Date.now() / 1000)) {
      return null;
    }

    // 无密钥时拒绝 token（fail-closed for cloud mode）
    if (!secret) return null;

    // WebCrypto HMAC-SHA256 签名验证
    const encoder = new TextEncoder();
    const secretBytes = encoder.encode(secret);
    const key = await crypto.subtle.importKey(
      'raw',
      secretBytes,
      { name: 'HMAC', hash: 'SHA-256' },
      false,
      ['verify'],
    );

    const data = encoder.encode(`${parts[0]}.${parts[1]}`);
    const signatureBytes = base64urlDecode(parts[2]!);
    const valid = await crypto.subtle.verify('HMAC', key, signatureBytes, data);
    if (!valid) return null;

    return payload;
  } catch {
    return null;
  }
}

function parseJwtHeader(headerSegment: string): JwtHeader | null {
  try {
    const json = decodeSegment(headerSegment);
    const parsed = JSON.parse(json) as JwtHeader;
    if (!parsed.alg) return null;
    return parsed;
  } catch {
    return null;
  }
}

/**
 * Base64url 解码：补齐 padding，安全解码为 Uint8Array。
 */
function base64urlDecode(segment: string): Uint8Array {
  let base64 = segment.replace(/-/g, '+').replace(/_/g, '/');
  const pad = base64.length % 4;
  if (pad === 2) base64 += '==';
  else if (pad === 3) base64 += '=';
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

/**
 * 解码 JWT segment 为 UTF-8 字符串。
 * 使用 Uint8Array + TextDecoder 避免 atob 的 Latin-1 问题。
 */
function decodeSegment(segment: string): string {
  const bytes = base64urlDecode(segment);
  return new TextDecoder().decode(bytes);
}

function decodeJwtPayload(payloadSegment: string): JwtPayload | null {
  try {
    const json = decodeSegment(payloadSegment);
    return JSON.parse(json) as JwtPayload;
  } catch {
    return null;
  }
}

/**
 * 构建转发到 Core / Tool Runtime 的认证头。
 * 将认证上下文中的 tenant_id 和 user_id 添加为自定义头。
 */
export function buildForwardHeaders(
  authContext: AuthContext | undefined,
  existing?: HeadersInit,
): Record<string, string> {
  const headers: Record<string, string> = {};
  if (existing) {
    if (Array.isArray(existing)) {
      for (const [key, value] of existing) {
        headers[key] = value;
      }
    } else if (existing instanceof Headers) {
      existing.forEach((value, key) => {
        headers[key] = value;
      });
    } else {
      Object.assign(headers, existing);
    }
  }

  if (authContext?.tenantId) {
    headers['x-tenant-id'] = authContext.tenantId;
  }
  if (authContext?.userId) {
    headers['x-user-id'] = authContext.userId;
  }

  return headers;
}

/**
 * 获取客户端真实 IP（考虑反向代理）。
 */
export function getClientIp(headers: Headers, trustProxy: boolean): string {
  if (trustProxy) {
    const forwarded = headers.get('x-forwarded-for');
    if (forwarded) {
      return forwarded.split(',')[0]!.trim();
    }
    const realIp = headers.get('x-real-ip');
    if (realIp) return realIp.trim();
  }
  return '127.0.0.1';
}

// --- Exported for testing ---
export { verifyJwt, base64urlDecode };
