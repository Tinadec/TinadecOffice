import type { Component } from 'vue'
import { ClipboardList, FileText, Network, ScanSearch, Sparkles, User, Users, Workflow } from '@lucide/vue'

/**
 * How a published agent mode looks in the mode pickers: its icon and its place in the list.
 *
 * Why this exists: every mode rendered with the same Sparkles icon, in whatever order Core
 * returned the rows, so the modes read as interchangeable names. Presentation is the Desktop's
 * job; what a mode does (roster, tools, prompts) stays in the pack and in Core.
 *
 * Keyed by the slug Core sends. For the bundled GraphSeedPack that slug is the manifest
 * resource_key, and `modePresentation.test.ts` pins every key here to a mode the manifest really
 * declares, so a renamed mode cannot silently fall back to the generic icon. A mode this table
 * does not know (user-authored, cloned, or from another pack) keeps the generic icon and sorts
 * after the known ones, in Core's order.
 *
 * Order: modes people already know from other agents come first (Solo, Plan), then the ones
 * that show the agent graph (Team, Review, Spec), then the declared-graph modes (Graph, Workflow).
 */
const PRESENTATION = new Map<string, { icon: Component; order: number }>([
  ['solo', { icon: User, order: 0 }],
  ['plan', { icon: ClipboardList, order: 1 }],
  ['free_director', { icon: Users, order: 2 }],
  ['review', { icon: ScanSearch, order: 3 }],
  ['spec', { icon: FileText, order: 4 }],
  ['vibe_graph', { icon: Network, order: 5 }],
  ['fixed_pipeline', { icon: Workflow, order: 6 }],
])

export const KNOWN_MODE_SLUGS: readonly string[] = [...PRESENTATION.keys()]

export function modeIcon(slug?: string | null): Component {
  return (slug ? PRESENTATION.get(slug)?.icon : undefined) ?? Sparkles
}

function rank(slug?: string | null): number {
  return (slug ? PRESENTATION.get(slug)?.order : undefined) ?? Number.MAX_SAFE_INTEGER
}

/** Known modes in their fixed order, then everything else in the order it arrived (sort is stable). */
export function sortModes<T extends { slug?: string | null }>(modes: readonly T[]): T[] {
  return [...modes].sort((a, b) => rank(a.slug) - rank(b.slug))
}
