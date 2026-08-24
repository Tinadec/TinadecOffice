/** Pass-through but ensures no secrets leak, keeps snake_case shape */
function sanitize(v: unknown): unknown {
  if (Array.isArray(v)) return v.map(sanitize);
  if (v && typeof v === 'object') {
    const rec = v as Record<string, unknown>;
    const out: Record<string, unknown> = {};
    for (const [k, val] of Object.entries(rec)) {
      const lower = k.toLowerCase();
      if (['api_key','apikey','access_token','secret','password','authorization','client_secret'].includes(lower)) continue;
      out[k] = sanitize(val);
    }
    return out;
  }
  return v;
}

export function mapReadiness(core: unknown): unknown {
  return sanitize(core);
}

export function mapModelReadiness(core: unknown): unknown {
  return sanitize(core);
}
