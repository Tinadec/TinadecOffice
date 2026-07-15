/**
 * Gateway 运行配置：本地模式 / 云端模式
 *
 * 本地模式（默认）：
 *   - 监听 127.0.0.1
 *   - 无认证
 *   - 无租户上下文
 *   - 单实例
 *
 * 云端模式：
 *   - 监听 0.0.0.0
 *   - 支持 API Key / JWT 认证
 *   - 支持租户上下文
 *   - 支持反向代理（X-Forwarded-* 头）
 *   - 支持横向扩展（无状态代理）
 */

export type DeployMode = 'local' | 'cloud';

export interface GatewayConfig {
  /** 部署模式 */
  mode: DeployMode;
  /** 监听端口 */
  port: number;
  /** 监听地址 */
  hostname: string;
  /** Core 服务 URL */
  coreUrl: string;
  /** Tool Runtime 服务 URL */
  toolRuntimeUrl: string;
  /** 认证配置（仅云端模式） */
  auth?: AuthConfig;
  /** CORS 允许的额外来源 */
  corsExtraOrigins: string[];
  /** 请求超时（毫秒） */
  requestTimeoutMs: number;
  /** 是否信任反向代理头 */
  trustProxy: boolean;
}

export interface AuthConfig {
  /** API Key 验证函数 */
  apiKeyValidator?: (key: string) => boolean;
  /** JWT 验证密钥 */
  jwtSecret?: string;
  /** 是否必须认证 */
  required: boolean;
}

function parseMode(value: string | undefined): DeployMode {
  return value === 'cloud' ? 'cloud' : 'local';
}

function getEnv(key: string, fallback: string): string {
  return process.env[key]?.trim() || fallback;
}

function getEnvNumber(key: string, fallback: number): number {
  const raw = process.env[key]?.trim();
  if (!raw) return fallback;
  const parsed = Number(raw);
  return Number.isFinite(parsed) ? parsed : fallback;
}

export function loadConfig(): GatewayConfig {
  const mode = parseMode(process.env.TINADEC_GATEWAY_MODE);
  const isCloud = mode === 'cloud';

  const port = getEnvNumber('TINADEC_GATEWAY_PORT', 48730);
  const hostname = isCloud ? '0.0.0.0' : '127.0.0.1';
  const coreUrl = getEnv('TINADEC_CORE_URL', 'http://127.0.0.1:48731');
  const toolRuntimeUrl = getEnv('TINADEC_TOOL_RUNTIME_URL', 'http://127.0.0.1:48732');

  const authRequired = isCloud && process.env.TINADEC_GATEWAY_AUTH_REQUIRED !== 'false';
  const jwtSecret = process.env.TINADEC_GATEWAY_JWT_SECRET;
  const apiKeyEnv = process.env.TINADEC_GATEWAY_API_KEY;

  const auth: AuthConfig | undefined = isCloud
    ? {
        required: authRequired,
        jwtSecret,
        apiKeyValidator: apiKeyEnv ? (key: string) => key === apiKeyEnv : undefined,
      }
    : undefined;

  const corsExtraOrigins: string[] = [];
  const extraOrigins = process.env.TINADEC_GATEWAY_CORS_ORIGINS?.trim();
  if (extraOrigins) {
    for (const origin of extraOrigins.split(',')) {
      const trimmed = origin.trim();
      if (trimmed) corsExtraOrigins.push(trimmed);
    }
  }

  return {
    mode,
    port,
    hostname,
    coreUrl,
    toolRuntimeUrl,
    auth,
    corsExtraOrigins,
    requestTimeoutMs: getEnvNumber('TINADEC_GATEWAY_TIMEOUT_MS', 120_000),
    trustProxy: isCloud,
  };
}

/** 运行时单例配置 */
let _config: GatewayConfig | null = null;

export function getConfig(): GatewayConfig {
  if (!_config) _config = loadConfig();
  return _config;
}
