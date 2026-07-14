import { coreEndpoint, proxyJson, type ProxyBody, type ProxyOptions } from './coreClient.js';

/**
 * Proxy a Debug API request to TinadecCore.
 * Reuses the same proxyJson pattern but with /api/v1/debug/* paths.
 */
export async function proxyDebugJson(path: string, options: ProxyOptions = {}) {
  return proxyJson(path, options);
}

/**
 * Build a Debug API WebSocket URL pointing to TinadecCore.
 */
export function debugWsUrl(): string {
  const baseUrl = process.env.TINADEC_CORE_URL ?? 'http://127.0.0.1:48731';
  return baseUrl.replace(/^http/, 'ws') + '/api/v1/debug/ws';
}
