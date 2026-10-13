/**
 * A model's reasoning text read as sections. Providers mark section titles as a bold line
 * (`**Checking the callers**`); those become headings and the newest closed one is the
 * status line while the model is still thinking. A title the stream has only half written
 * (`**Checking the cal`) is held back until it closes, so the status never flickers through
 * partial words.
 */
export interface ReasoningSection {
  title: string | null
  body: string
}

const HEADING = /^\s*\*\*(.+?)\*\*\s*$/

export function reasoningSections(text: string): ReasoningSection[] {
  const lines = text.split('\n')
  // An unterminated bold line at the very end is a heading still being typed.
  const last = lines.at(-1) ?? ''
  if (/^\s*\*\*/.test(last) && !HEADING.test(last)) lines.pop()

  const sections: ReasoningSection[] = []
  let current: ReasoningSection = { title: null, body: '' }
  for (const line of lines) {
    const heading = HEADING.exec(line)
    if (heading) {
      if (current.title !== null || current.body.trim()) sections.push(current)
      current = { title: heading[1]!.trim(), body: '' }
      continue
    }
    current.body = current.body ? `${current.body}\n${line}` : line
  }
  if (current.title !== null || current.body.trim()) sections.push(current)
  return sections.map((section) => ({ title: section.title, body: section.body.trim() }))
}

/** The newest closed section title, for the "currently thinking about" line. */
export function latestReasoningTitle(text: string): string | null {
  const sections = reasoningSections(text)
  for (let i = sections.length - 1; i >= 0; i -= 1) {
    if (sections[i]!.title) return sections[i]!.title
  }
  return null
}
