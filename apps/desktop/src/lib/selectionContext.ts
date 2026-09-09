/**
 * Pure resolution of "what did the user right-click on", used by
 * SelectionContextMenu to decide which actions are offered.
 *
 * Kept DOM-light and side-effect-free so the action matrix is unit-testable
 * without mounting components. The selectable content list must stay in sync
 * with the `user-select: text` opt-in block in styles.css (guarded by
 * selectionContext.test.ts), otherwise a surface can be selectable but the
 * menu would not recognise it as content.
 */

/** Surfaces that opt back into text selection (mirrors the styles.css list). */
export const SELECTABLE_CONTENT_SELECTORS = [
  '.message-content',
  '.markdown-body',
  '.detail-dialog',
  '.composer-error',
  '.tool-call-card',
  '.tool-result-viewer',
  '.thinking-process',
  '.terminal-call-block',
  '.commit-compare',
  '.diff-viewer',
  '.inspector-panel',
  '.search-results',
  'pre',
  'code',
] as const

export type SelectionSurface = 'terminal' | 'field' | 'content' | 'chrome'

export interface SelectionContext {
  surface: SelectionSurface
  /** Plain text of the current selection (empty when collapsed). */
  text: string
  hasSelection: boolean
  /** Target is an editable field that accepts cut/paste. */
  editable: boolean
  /** Target is a read-only input/textarea/contenteditable. */
  readOnly: boolean
  /** http(s) URL from the selection or the nearest ancestor anchor. */
  link: string | null
}

/** Element selectors that accept keyboard/insertion edits. */
const FIELD_SELECTOR = 'input, textarea, [contenteditable=""], [contenteditable="true"]'

/** Read-only field shapes that still allow copy/select-all but not cut/paste. */
function isReadOnlyField(el: Element): boolean {
  if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
    return el.readOnly || el.disabled
  }
  if (el.getAttribute('contenteditable') === 'false') return true
  return false
}

/** Only http/https links are offered, so a `javascript:` href can never reach the opener. */
export function detectLink(target: Element | null, text: string): string | null {
  const anchor = target?.closest('a[href]') as HTMLAnchorElement | null
  const href = anchor?.href ?? ''
  if (/^https?:\/\//i.test(href)) return href
  const trimmed = text.trim()
  if (/^https?:\/\/\S+$/i.test(trimmed)) return trimmed
  return null
}

export function resolveSelectionContext(target: Element | null, selection: Selection | null): SelectionContext {
  const domText = selection?.toString() ?? ''

  const base: SelectionContext = {
    surface: 'chrome',
    text: domText,
    hasSelection: domText.trim().length > 0,
    editable: false,
    readOnly: false,
    link: detectLink(target, domText),
  }

  if (!target) return base

  // Terminal: xterm paints into a canvas, so only its own API can read or
  // replace the selection — the DOM selection is always empty there.
  if (target.closest('.xterm')) {
    return { ...base, surface: 'terminal', text: '', hasSelection: false }
  }

  const field = target.closest(FIELD_SELECTOR)
  if (field) {
    const readOnly = isReadOnlyField(field)
    // Inputs/textarea expose their range on the element, not on window.getSelection().
    const fieldText = field instanceof HTMLInputElement || field instanceof HTMLTextAreaElement
      ? field.value.slice(field.selectionStart ?? 0, field.selectionEnd ?? 0)
      : domText
    return {
      ...base,
      surface: 'field',
      text: fieldText,
      hasSelection: fieldText.trim().length > 0,
      editable: !readOnly,
      readOnly,
      link: detectLink(target, fieldText),
    }
  }

  for (const selector of SELECTABLE_CONTENT_SELECTORS) {
    if (target.closest(selector)) return { ...base, surface: 'content' }
  }

  return base
}

export interface SelectionMenuAction {
  key: 'copy' | 'cut' | 'paste' | 'selectAll' | 'copyLink' | 'openLink' | 'clearTerminal'
  /** Separator is drawn before this item when true. */
  group?: boolean
}

/**
 * Action matrix per surface. Terminal paste/select-all are always available
 * because xterm owns its own buffer; field cut/paste require an editable target.
 */
export function buildSelectionMenuActions(context: SelectionContext): SelectionMenuAction[] {
  const actions: SelectionMenuAction[] = []

  // Plain chrome with nothing selected has no text to act on; returning no
  // actions is what keeps the menu from appearing as noise over ordinary UI.
  if (context.surface === 'chrome' && !context.hasSelection) return actions

  if (context.surface === 'terminal') {
    actions.push({ key: 'copy' }, { key: 'paste' }, { key: 'selectAll' })
    if (context.link) actions.push({ key: 'copyLink', group: true }, { key: 'openLink' })
    actions.push({ key: 'clearTerminal', group: true })
    return actions
  }

  if (context.hasSelection) actions.push({ key: 'copy' })
  if (context.surface === 'field' && context.editable) {
    if (context.hasSelection) actions.push({ key: 'cut' })
    actions.push({ key: 'paste' })
  }
  actions.push({ key: 'selectAll' })

  if (context.link) {
    actions.push({ key: 'copyLink', group: true }, { key: 'openLink' })
  }

  return actions
}
