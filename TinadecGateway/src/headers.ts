/** Thin proxy correlation headers: X-Request-Id + X-Tinadec-Principal: dev@local */
export const PRINCIPAL_VALUE = 'dev@local';

export function ensureRequestId(incoming?: string | null): string {
  if (incoming && incoming.trim().length > 0) return incoming.trim();
  // crypto.randomUUID is available in Bun/Node 19+
  return crypto.randomUUID();
}

export function buildProxyHeaders(incomingHeaders?: Headers): Record<string, string> {
  const requestId = ensureRequestId(incomingHeaders?.get('x-request-id') ?? incomingHeaders?.get('X-Request-Id'));
  return {
    'x-request-id': requestId,
    'x-tinadec-principal': PRINCIPAL_VALUE,
  };
}

export function proxyHeadersForFetch(incoming?: Headers): HeadersInit {
  return buildProxyHeaders(incoming);
}
