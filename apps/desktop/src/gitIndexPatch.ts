import type { GitDiffFile, GitDiffHunk, GitDiffLine, GitParsedDiff } from './gitDiffParser'

interface PositionedLine {
  line: GitDiffLine
  oldStart: number
  newStart: number
}

/**
 * Produce independently-applicable text hunks for selected change blocks.
 * A replacement's adjacent delete/add lines remain atomic so a line selection
 * cannot accidentally turn a replacement into a duplicate insertion.
 */
export function buildGitIndexPatch(parsed: GitParsedDiff, selectedLineIds: ReadonlySet<string>): string | null {
  const files = parsed.files
    .map((file) => buildFilePatch(file, selectedLineIds))
    .filter((patch): patch is string => Boolean(patch))
  return files.length > 0 ? files.join('') : null
}

export function changeBlockLineIds(hunk: GitDiffHunk, lineId: string): string[] {
  const index = hunk.lines.findIndex((line) => line.id === lineId)
  if (index < 0 || hunk.lines[index]?.change === 'context') return []
  let start = index
  let end = index
  while (start > 0 && hunk.lines[start - 1]?.change !== 'context') start--
  while (end + 1 < hunk.lines.length && hunk.lines[end + 1]?.change !== 'context') end++
  return hunk.lines.slice(start, end + 1).map((line) => line.id)
}

function buildFilePatch(file: GitDiffFile, selectedLineIds: ReadonlySet<string>): string | null {
  if (file.binary || file.change_type !== 'modified') return null
  const hunks = file.hunks.map((hunk) => buildHunkPatches(hunk, selectedLineIds)).flat()
  if (hunks.length === 0) return null
  return `diff --git a/${file.path} b/${file.path}\n--- a/${file.path}\n+++ b/${file.path}\n${hunks.join('')}`
}

function buildHunkPatches(hunk: GitDiffHunk, selectedLineIds: ReadonlySet<string>): string[] {
  const positioned = positionLines(hunk)
  const selectedBlocks = changeBlocks(positioned)
    .filter((block) => block.some((entry) => selectedLineIds.has(entry.line.id)))
  if (selectedBlocks.length === 0) return []

  const ranges = selectedBlocks.map((block) => {
    const first = positioned.indexOf(block[0]!)
    const last = positioned.indexOf(block.at(-1)!)
    return expandContext(positioned, first, last)
  })
  return mergeRanges(ranges).map(([start, end]) => formatHunk(positioned.slice(start, end + 1)))
}

function positionLines(hunk: GitDiffHunk): PositionedLine[] {
  let oldPosition = hunk.old_start
  let newPosition = hunk.new_start
  return hunk.lines.map((line) => {
    const value = { line, oldStart: oldPosition, newStart: newPosition }
    if (line.change !== 'add') oldPosition++
    if (line.change !== 'delete') newPosition++
    return value
  })
}

function changeBlocks(lines: PositionedLine[]): PositionedLine[][] {
  const blocks: PositionedLine[][] = []
  let current: PositionedLine[] = []
  for (const line of lines) {
    if (line.line.change === 'context') {
      if (current.length > 0) blocks.push(current)
      current = []
    } else {
      current.push(line)
    }
  }
  if (current.length > 0) blocks.push(current)
  return blocks
}

function expandContext(lines: PositionedLine[], start: number, end: number): [number, number] {
  let from = start
  let before = 0
  while (from > 0 && before < 3 && lines[from - 1]?.line.change === 'context') { from--; before++ }
  let to = end
  let after = 0
  while (to + 1 < lines.length && after < 3 && lines[to + 1]?.line.change === 'context') { to++; after++ }
  return [from, to]
}

function mergeRanges(ranges: Array<[number, number]>): Array<[number, number]> {
  return ranges.sort(([left], [right]) => left - right).reduce<Array<[number, number]>>((merged, range) => {
    const previous = merged.at(-1)
    if (previous && range[0] <= previous[1] + 1) previous[1] = Math.max(previous[1], range[1])
    else merged.push(range)
    return merged
  }, [])
}

function formatHunk(lines: PositionedLine[]): string {
  const first = lines[0]!
  const oldCount = lines.filter((line) => line.line.change !== 'add').length
  const newCount = lines.filter((line) => line.line.change !== 'delete').length
  const body = lines.map(({ line }) => `${line.change === 'add' ? '+' : line.change === 'delete' ? '-' : ' '}${line.content}`).join('\n')
  return `@@ -${formatRange(first.oldStart, oldCount)} +${formatRange(first.newStart, newCount)} @@\n${body}\n`
}

function formatRange(start: number, count: number): string {
  return count === 1 ? String(start) : `${start},${count}`
}
