export function mapHealth(core: unknown, gatewayMeta: Record<string, unknown>): Record<string, unknown> {
  const c = (core && typeof core === 'object' ? core as Record<string, unknown> : {});
  return {
    ...c,
    gateway: 'ok',
    ...gatewayMeta,
  };
}
