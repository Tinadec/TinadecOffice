/**
 * 认证与租户上下文中间件。
 *
 * 本地模式：跳过认证，不提取租户上下文。
 * 云端模式：
 *   - 从 Authorization 头提取 Bearer token 或 API Key
 *   - 验证 JWT 或 API Key
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
 */
export function authenticate(
  headers: Headers,
  authConfig?: AuthConfig,
): AuthResult {
  // 本地模式：跳过认证
  if (!authConfig) {
    return { ok: true, context: { authenticated: true } };
  }

  // 公共路径：跳过认证
  // 注意：路径检查在调用方处理，这里只做 token 验证

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
    const decoded = verifyJwt(token, authConfig.jwtSecret);
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

interface JwtPayload {
  sub?: string;
  user_id?: string;
  tenant_id?: string;
  roles?: string[];
  exp?: number;
}

/**
 * 简单的 JWT 验证（HS256）。
 * Bun 原生支持 WebCrypto API。
 */
function verifyJwt(token: string, secret?: string): JwtPayload | null {
  if (!secret) {
    // 无密钥配置时，尝试解码但不验证签名（仅用于开发环境）
    return decodeJwtPayload(token);
  }

  const parts = token.split('.');
  if (parts.length !== 3) return null;

  try {
    const payload = decodeJwtPayload(token);
    if (!payload) return null;

    // 检查过期时间
    if (payload.exp && payload.exp < Math.floor(Date.now() / 1000)) {
      return null;
    }

    // TODO: 使用 WebCrypto API 验证 HS256 签名
    // 当前仅做 payload 解码和过期检查，签名验证在后续迭代中补全
    return payload;
  } catch {
    return null;
  }
}

function decodeJwtPayload(token: string): JwtPayload | null {
  try {
    const parts = token.split('.');
    if (parts.length !== 3) return null;
    const payload = parts[1]!;
    // Base64url → Base64 → JSON
    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/');
    const json = atob(base64);
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
