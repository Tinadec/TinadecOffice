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
export type TrustProxyMode = 'false' | 'true' | 'cloudflare';

export interface GatewayConfig {
  /** 部署模式 */
  mode: DeployMode;
  /** 监听端口 */
  port: number;
  /** 监听地址 */
  hostname: string;
  /** Core 服务 URL */
  coreUrl: string;
  /** Tool Runtime 服务 URL；空字符串 = 未配置独立工具运行时（Core 工具 provider 为权威入口） */
  toolRuntimeUrl: string;
  /** 认证配置（仅云端模式） */
  auth?: AuthConfig;
  /** CORS 允许的额外来源 */
  corsExtraOrigins: string[];
  /** 请求超时（毫秒）。注意：当前没有代理路径消费它，设置它不会产生任何效果，见下方注释。 */
  requestTimeoutMs: number;
  /** 请求耗时日志采样率。0 = 只出 Server-Timing 响应头，不写日志。 */
  perfSampleRate: number;
  /** 是否信任反向代理头 */
  trustProxy: boolean;
  /** 反向代理信任模式：false | true | cloudflare */
  trustProxyMode: TrustProxyMode;
  /** Peer mTLS 监听绑定地址（默认 0.0.0.0，仅在 inbound 开启时生效） */
  peerBindHost: string;
  /** 可选覆盖 Peer mTLS 起始监听端口（TINADEC_PEER_LISTEN_PORT） */
  peerListenPortOverride?: number;
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

/** 一个越界的采样率会让每个请求都写日志，或让日志完全消失，两者都不诚实。 */
function clampRate(value: number): number {
  if (!Number.isFinite(value) || value <= 0) return 0;
  return value >= 1 ? 1 : value;
}

export function loadConfig(): GatewayConfig {
  const mode = parseMode(process.env.TINADEC_GATEWAY_MODE);
  const isCloud = mode === 'cloud';

  const port = getEnvNumber('TINADEC_GATEWAY_PORT', 48730);
  const hostname = isCloud ? '0.0.0.0' : '127.0.0.1';
  const coreUrl = getEnv('TINADEC_CORE_URL', 'http://127.0.0.1:48731');
  // No silent default: a standalone Tool Runtime is opt-in. When unset, the
  // /api/v1/tool-runtime/* read routes fall back to Core's tool provider.
  const toolRuntimeUrl = getEnv('TINADEC_TOOL_RUNTIME_URL', '');

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

  const rawTrustProxy = (process.env.TINADEC_TRUST_PROXY ?? '').trim().toLowerCase();
  const trustProxyMode: TrustProxyMode =
    rawTrustProxy === 'cloudflare'
      ? 'cloudflare'
      : rawTrustProxy === 'true' || rawTrustProxy === '1' || isCloud
        ? 'true'
        : 'false';

  return {
    mode,
    port,
    hostname,
    coreUrl,
    toolRuntimeUrl,
    auth,
    corsExtraOrigins,
    // Not enforced anywhere yet: no proxy path reads this value, so a hung upstream holds the
    // downstream connection open forever and `TINADEC_GATEWAY_TIMEOUT_MS` changes nothing.
    // Wiring it needs an AbortSignal on every upstream fetch, which has to exempt the SSE and
    // streaming proxies (they are meant to stay open) — that is a behaviour change of its own,
    // not a bug fix, so it stays a separate item rather than riding along silently.
    requestTimeoutMs: getEnvNumber('TINADEC_GATEWAY_TIMEOUT_MS', 120_000),
    trustProxy: trustProxyMode !== 'false',
    trustProxyMode,
    // 默认 0：观测不能以日志 IO 为代价。排查性能时再显式打开（0.05 = 5%）。
    perfSampleRate: clampRate(getEnvNumber('TINADEC_GATEWAY_PERF_SAMPLE', 0)),
    peerBindHost: getEnv('TINADEC_PEER_BIND_HOST', '0.0.0.0'),
    ...(process.env.TINADEC_PEER_LISTEN_PORT?.trim()
      ? { peerListenPortOverride: getEnvNumber('TINADEC_PEER_LISTEN_PORT', 48735) }
      : {}),
  };
}

/** 运行时单例配置 */
let _config: GatewayConfig | null = null;

export function getConfig(): GatewayConfig {
  if (!_config) _config = loadConfig();
  return _config;
}
