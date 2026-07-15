/**
 * WebSocket 代理：终端、调试、协作。
 *
 * Gateway 作为 WebSocket 反向代理，将客户端的 WebSocket 连接
 * 透传到 Core 或 Tool Runtime 的 WebSocket 端点。
 *
 * 用途：
 *   - 终端 PTY（→ Tool Runtime）
 *   - 调试器通信（→ Core Debug Studio）
 *   - 实时协作（→ Core）
 *
 * Bun 原生支持 WebSocket（通过 Server 对象的 upgrade 方法）。
 */

import { getConfig } from './config.js';
import { coreUrl } from './coreClient.js';
import { toolRuntimeUrl } from './toolRuntimeClient.js';

export type WebSocketTarget = 'core' | 'tool_runtime';

export interface WebSocketProxyConfig {
  /** 目标服务 */
  target: WebSocketTarget;
  /** 目标 WebSocket 路径 */
  path: string;
  /** 子协议 */
  protocols?: string[];
  /** 转发头 */
  forwardHeaders?: Record<string, string>;
}

/**
 * 构建目标 WebSocket URL。
 */
export function buildTargetWsUrl(config: WebSocketProxyConfig, query?: URLSearchParams): string {
  const baseUrl = config.target === 'core' ? coreUrl() : toolRuntimeUrl();
  const wsBase = baseUrl.replace(/^http/, 'ws');
  const url = new URL(config.path, wsBase);
  if (query) {
    for (const [key, value] of query.entries()) {
      url.searchParams.set(key, value);
    }
  }
  return url.toString();
}

/**
 * WebSocket 代理路由配置表。
 * 定义了 Gateway 暴露的 WebSocket 端点到目标服务的映射。
 */
export const WS_ROUTES: Record<string, WebSocketProxyConfig> = {
  /** 终端 PTY → Tool Runtime */
  '/ws/terminal': {
    target: 'tool_runtime',
    path: '/ws/terminal',
  },
  /** 调试器 WebSocket → Core */
  '/ws/debug': {
    target: 'core',
    path: '/api/v1/debug/ws',
  },
  /** 协作 WebSocket → Core */
  '/ws/collaboration': {
    target: 'core',
    path: '/ws/collaboration',
  },
};

/**
 * 查找 WebSocket 路由配置。
 */
export function findWsRoute(path: string): WebSocketProxyConfig | null {
  return WS_ROUTES[path] ?? null;
}

/**
 * 为 Bun WebSocket 服务器创建消息处理器。
 * 在客户端和目标服务之间双向透传消息。
 */
export interface WsProxyHandlers {
  /** 客户端 → 目标 */
  onClientMessage: (ws: { send: (data: string | ArrayBuffer) => void }, message: string | ArrayBuffer) => void;
  /** 连接关闭 */
  onClose: (code?: number, reason?: string) => void;
  /** 错误处理 */
  onError: (error: unknown) => void;
}

/**
 * 创建一个简单的 WebSocket 消息透传处理器。
 * 使用 Bun 的 WebSocket 客户端连接到目标服务。
 */
export function createWsProxyHandlers(targetUrl: string): WsProxyHandlers {
  // Bun WebSocket 客户端
  let targetWs: WebSocket | null = null;
  let clientWs: { send: (data: string | ArrayBuffer) => void } | null = null;

  try {
    targetWs = new WebSocket(targetUrl);

    targetWs.onmessage = (event) => {
      if (clientWs) {
        const data = typeof event.data === 'string' ? event.data : event.data;
        clientWs.send(data as string);
      }
    };

    targetWs.onclose = () => {
      // 目标关闭时，通知客户端
    };

    targetWs.onerror = () => {
      // 目标错误时，通知客户端
    };
  } catch {
    // 目标连接失败
  }

  return {
    onClientMessage: (_ws, message) => {
      clientWs = _ws;
      if (targetWs && targetWs.readyState === WebSocket.OPEN) {
        targetWs.send(message);
      }
    },
    onClose: () => {
      if (targetWs) {
        targetWs.close();
        targetWs = null;
      }
    },
    onError: () => {
      if (targetWs) {
        targetWs.close();
        targetWs = null;
      }
    },
  };
}
