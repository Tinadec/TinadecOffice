/**
 * @deprecated Gateway 薄代理已移除聚合/binding，直接 404；保留文件仅为兼容旧 import，勿新增引用
 * 新流程：所有 /agents、/agent-modes、/prompt-pipelines、/interactions 等直接 proxyJson 到 Core
 */
import type { ProxyOptions, ProxyResult } from './coreClient.js';

export type CoreJsonFetcher = (path: string, options?: ProxyOptions) => Promise<ProxyResult>;

// deprecated shims — thin proxy no longer exposes BFF aggregation; keep export shape to avoid breaking old import sites
export function aggregateModelCenter(): never { throw new Error('deprecated: use Core /api/v1/* thin proxy'); }
export function aggregateAgentCenter(): never { throw new Error('deprecated: use Core /api/v1/* thin proxy'); }
export function validateAgentRuntimeBindingInput(_input: unknown): { ok: false; errors: string[] } { return { ok: false, errors: ['deprecated'] }; }
export function agentRuntimeBindingWriteResult(_agentId: unknown, _input: unknown): ProxyResult {
  return { status: 404, data: { code: 'not_found', message: 'Agent runtime binding is removed; use Core thin proxy.' } };
}
export async function loadModelCenterOverview(): Promise<ProxyResult> {
  return { status: 404, data: { code: 'not_found', message: 'model-center/overview removed' } };
}
export async function loadAgentCenterOverview(): Promise<ProxyResult> {
  return { status: 404, data: { code: 'not_found', message: 'agent-center/overview removed' } };
}
export function modelDiscoveryRefreshResult(): ProxyResult { return { status: 404, data: { code: 'not_found', message: 'removed' } }; }
