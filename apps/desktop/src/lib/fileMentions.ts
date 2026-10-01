import type { DirEntryView } from './workspaceSearch'

/**
 * `@` completion in the composer. It completes one path segment at a time because
 * the only surface the tool manifest offers is `ls`, which lists one directory.
 * There is no filename index, so this deliberately does not do fuzzy whole-workspace
 * matching — that would be a second fake capability.
 */
export interface MentionToken {
  /** Index of the `@` in the draft. */
  from: number
  /** Directory being completed inside, relative to the workspace root ('' = root). */
  directory: string
  /** Text after the last slash: the prefix each candidate must start with. */
  query: string
}

export function parseMentionToken(text: string, caret: number): MentionToken | null {
  const at = text.slice(0, caret).lastIndexOf('@')
  if (at < 0) return null
  // An @ only starts a mention where a word could start; in the middle of `a@b` it is
  // an email address, an npm scope, or part of an identifier.
  if (at > 0 && !/[\s([]/.test(text[at - 1]!)) return null
  const fragment = text.slice(at + 1, caret)
  if (/\s/.test(fragment)) return null
  const slash = fragment.lastIndexOf('/')
  if (slash < 0) return { from: at, directory: '', query: fragment }
  return { from: at, directory: fragment.slice(0, slash + 1), query: fragment.slice(slash + 1) }
}

export function filterMentionEntries(
  entries: readonly DirEntryView[],
  query: string,
  limit = 8,
): DirEntryView[] {
  const needle = query.toLowerCase()
  const scored: Array<{ entry: DirEntryView; rank: number }> = []
  for (const entry of entries) {
    const name = entry.name.toLowerCase()
    if (!needle) {
      scored.push({ entry, rank: 2 })
    } else if (name.startsWith(needle)) {
      scored.push({ entry, rank: 0 })
    } else if (name.includes(needle)) {
      scored.push({ entry, rank: 1 })
    }
  }
  return scored
    .sort((a, b) => a.rank - b.rank || a.entry.name.localeCompare(b.entry.name))
    .slice(0, limit)
    .map((row) => row.entry)
}

/**
 * Replace the fragment the user typed with the chosen entry. Directories keep their
 * trailing slash and keep the menu open, so the next segment is one Tab away; a file
 * is finished and gets a space so the sentence continues.
 */
export function completeMentionToken(
  text: string,
  caret: number,
  token: MentionToken,
  entry: DirEntryView,
): { text: string; caret: number } {
  // A directory keeps its slash so the next segment is one Tab away; a file gets a
  // space, unless the user already typed one — completing must not widen the gap.
  const suffix = entry.isDir
    ? '/'
    : (/\s/.test(text.slice(caret, caret + 1)) ? '' : ' ')
  const inserted = token.directory + entry.name + suffix
  const next = text.slice(0, token.from + 1) + inserted + text.slice(caret)
  const position = token.from + 1 + inserted.length
  return { text: next, caret: position }
}
