/**
 * Which side of the index a `git status` row belongs to.
 *
 * TinadecTools mints `staged_status` / `unstaged_status` through `StatusLabel`, so the
 * values are words (`added`, `modified`, `clean`, `untracked`, …), not the porcelain
 * characters they came from. Comparing them against `' '` / `'?'` therefore matched
 * nothing: every row read as unstaged while the header counted every row as staged.
 *
 * Structural on purpose — `lib` does not import from `composables`, so this stays
 * testable without dragging the transport layer (and its `window` access) into node.
 */
export interface GitStatusSides {
  status?: string
  staged_status?: string
  unstaged_status?: string
  is_untracked?: boolean
}

/**
 * A column marking that says "this side did not change". Both vocabularies are listed:
 * the labels the provider emits and the raw characters fixtures and hand-built
 * payloads still carry.
 */
const NO_CHANGE_MARKS: readonly string[] = ['clean', 'untracked', ' ', '?', '']

function sideChanged(mark: string | null | undefined): boolean {
  return typeof mark === 'string' && !NO_CHANGE_MARKS.includes(mark)
}

/** True when the index differs from HEAD, i.e. the row belongs under "Staged Changes". */
export function isStagedFile(file: GitStatusSides): boolean {
  if (sideChanged(file.staged_status)) return true
  return typeof file.status === 'string'
    && (file.status === 'staged_and_modified' || file.status.startsWith('staged_'))
}

/**
 * True when the row belongs under "Changes": the worktree differs, or the file is not in
 * the index at all. An untracked row has no changed side, and counting it here is what
 * keeps it from vanishing out of both sections. A file that is staged and then modified
 * again answers true here too, exactly as `git status` shows it.
 */
export function isUnstagedFile(file: GitStatusSides): boolean {
  if (sideChanged(file.unstaged_status)) return true
  if (file.is_untracked === true || file.status === 'untracked' || file.status === '?') return true
  // Fixtures and hand-built payloads carry only the summary column; reading those as
  // "nothing changed" would drop the row out of both sections.
  return file.staged_status == null && file.unstaged_status == null && sideChanged(file.status)
}
