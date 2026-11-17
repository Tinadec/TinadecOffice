/**
 * Modes that older packs installed but the product no longer offers. `auto` was
 * a catch-all profile with no distinct topology or behavior; every real
 * collaboration shape is expressed by Solo / Team / Pipeline and the graph
 * editor. Old workspaces may still carry the row, so the UI filters it out
 * rather than presenting a mode the product cannot explain.
 */
export const RETIRED_MODE_SLUGS: ReadonlySet<string> = new Set(['conversation.auto'])

export interface RetirableMode {
  slug?: string | null
  display_name?: string | null
}

export function isRetiredMode(mode: RetirableMode): boolean {
  const slug = (mode.slug ?? '').trim().toLowerCase()
  if (RETIRED_MODE_SLUGS.has(slug)) return true
  // Legacy rows occasionally lost the slug but kept the label.
  return !slug && (mode.display_name ?? '').trim().toLowerCase() === 'auto'
}

export function withoutRetiredModes<T extends RetirableMode>(modes: readonly T[]): T[] {
  return modes.filter(mode => !isRetiredMode(mode))
}
