import { ref, type Ref } from 'vue'
import { sessionStorageId } from './storageScope'

/**
 * Which work sessions currently have a desktop partner window out. Module-level so the command
 * palette and the composer share one answer to "is this session's partner out", and the label can
 * read toggle state without asking the main process on every render. The main process owns the
 * windows themselves; this only mirrors the toggle receipts.
 */
export const activeSessionPartners: Ref<ReadonlySet<string>> = ref(new Set<string>())

export function isSessionPartnerActive(sessionId: string | null | undefined): boolean {
  return Boolean(sessionId) && activeSessionPartners.value.has(sessionId as string)
}

/**
 * Toggle the current session's partner window. No session or no desktop host (browser preview)
 * is a no-op rather than an error: the command stays harmless where it cannot open a window.
 *
 * The storage scope travels with the toggle. A session in a project lives in that project's
 * store, and the partner window cannot work it out for itself — it is not a trusted host, has no
 * scope registry, and every read it makes goes through the main-process proxy. Without this the
 * proxy's reads land in the user store, where a project session simply does not exist.
 */
export async function toggleSessionPartner(sessionId: string | null | undefined): Promise<void> {
  if (!sessionId) return
  const bridge = typeof window === 'undefined' ? undefined : window.tinadec?.sessionPartner
  if (!bridge) return
  const result = await bridge.toggle(sessionId, sessionStorageId(sessionId))
  const next = new Set(activeSessionPartners.value)
  if (result.active) next.add(sessionId)
  else next.delete(sessionId)
  activeSessionPartners.value = next
}

/** Test seam: forget every mirrored toggle state. */
export function resetSessionPartners(): void {
  activeSessionPartners.value = new Set<string>()
}