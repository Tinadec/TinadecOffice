import { shallowRef } from 'vue'
import { api } from '@/api'

/**
 * Display names for agent slugs ("search" → "Search"). Read once per window; a failed or
 * slow read leaves names as their slugs instead of blocking the feed, and the next caller
 * after a failure may try again.
 */
const names = shallowRef<ReadonlyMap<string, string>>(new Map())
let loaded: Promise<void> | null = null

function ensureLoaded(): void {
  if (loaded) return
  loaded = api.listAgents()
    .then((agents) => {
      names.value = new Map(agents.filter((agent) => agent.slug && agent.display_name).map((agent) => [agent.slug, agent.display_name]))
    })
    .catch(() => {
      loaded = null
    })
}

export function useAgentDirectory() {
  ensureLoaded()
  /** The display name of a slug or a handle (`search#2` → "Search #2"); falls back to the input. */
  function displayName(slugOrHandle: string | null | undefined): string {
    if (!slugOrHandle) return ''
    const [slug, ordinal] = slugOrHandle.split('#')
    const name = names.value.get(slug!) ?? slug!
    return ordinal ? `${name} #${ordinal}` : name
  }
  return { displayName }
}

/** For tests. */
export function resetAgentDirectory(): void {
  names.value = new Map()
  loaded = null
}
