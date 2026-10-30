import type { UiePageId } from '../../../engine/types'

/**
 * Which pages render the shared nav card in its companion domain.
 *
 * The companion centre is the only page that is about companions, and it must show the same
 * sidebar island as the work pages — same brand header, same footer — with companion entries in
 * the middle instead of projects and sessions. Kept as a pure function so the answer is testable
 * without mounting the card.
 */
export function isCompanionDomainPage(pageId: UiePageId | undefined): boolean {
  return pageId === 'companionCenter'
}
