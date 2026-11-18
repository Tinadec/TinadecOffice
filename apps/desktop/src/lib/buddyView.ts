import { ref, type Ref } from 'vue'

/**
 * Whether the one buddy conversation is currently out.
 *
 * The buddy is a single conversation, so this is a boolean rather than the set sessionPartner
 * keeps: there is nothing to key it by. Module-level so the sidebar, the flat chat card and the
 * space drawer all give the same answer to "is the buddy out".
 */
export const buddyViewActive: Ref<boolean> = ref(false)

/** The sidebar entry and the nav card are toggles: pressing the buddy entry again puts it away. */
export function toggleBuddyView(): void {
  buddyViewActive.value = !buddyViewActive.value
}

/** A deep link is not a toggle: entering /buddy must open the view even if it was already open. */
export function openBuddyView(): void {
  buddyViewActive.value = true
}

export function closeBuddyView(): void {
  buddyViewActive.value = false
}

/** Test seam: forget the toggle state. */
export function resetBuddyView(): void {
  buddyViewActive.value = false
}
