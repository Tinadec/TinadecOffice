import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { isStagedFile, isUnstagedFile, type GitStatusSides } from './gitStatusSides'

/**
 * The two sections of the Git panel are decided from `staged_status` /
 * `unstaged_status`, and those strings are minted by TinadecTools as *labels*
 * (`StatusLabel`), not as porcelain characters. Comparing them against `' '`/`'?'` — which
 * is what this layer used to do — matched nothing, so every row looked unstaged while the
 * header counted every row as staged. These cases build entries the way the mint site does
 * and read the label table out of that file, so a rename there fails here.
 */
const toolSource = readFileSync(
  fileURLToPath(new URL('../../../../TinadecTools/Tools/Git/GitReadTools.cs', import.meta.url)),
  'utf8',
)

const switchBody = /static string StatusLabel\(char code\) => code switch \{([^}]*)\}/.exec(toolSource)?.[1]
const MARKS = [...(switchBody ?? '').matchAll(/'(.)' => "([^"]+)"/g)].map((m) => ({ code: m[1]!, label: m[2]! }))

function labelOf(code: string): string {
  return MARKS.find((mark) => mark.code === code)?.label ?? code
}

/** Mirrors `GitReadTools.ParseStatusEntry` for one porcelain v2 column pair. */
function entry(staged: string, unstaged: string): GitStatusSides {
  const conflicted = staged === 'U' || unstaged === 'U'
    || ['AA', 'DD', 'AU', 'UA', 'DU', 'UD'].includes(`${staged}${unstaged}`)
  const untracked = staged === '?' && unstaged === '?'
  const status = conflicted
    ? 'conflicted'
    : untracked
      ? 'untracked'
      : staged !== ' ' && unstaged !== ' '
        ? 'staged_and_modified'
        : staged !== ' '
          ? `staged_${labelOf(staged)}`
          : labelOf(unstaged)
  return {
    staged_status: labelOf(staged),
    unstaged_status: labelOf(unstaged),
    status,
    is_untracked: untracked,
  }
}

const CASES: Array<[staged: string, unstaged: string, expectStaged: boolean, expectUnstaged: boolean]> = [
  ['M', ' ', true, false], // staged modification only
  ['A', ' ', true, false],
  ['D', ' ', true, false],
  ['R', ' ', true, false],
  ['C', ' ', true, false],
  [' ', 'M', false, true], // worktree modification only
  [' ', 'D', false, true],
  ['M', 'M', true, true], // staged and then modified again: both sections, as in `git status`
  ['?', '?', false, true], // untracked must not vanish from the panel
  ['U', 'U', true, true], // conflicted needs a decision either way
  [' ', ' ', false, false], // never emitted by `git status`; nothing to show
]

describe('git status side labels', () => {
  it('reads the label table from the file that mints it', () => {
    expect(switchBody, 'StatusLabel no longer has the shape this test can read').toBeTruthy()
    expect(MARKS.length).toBeGreaterThan(4)
    // The two marks that mean "this side did not change" are named here on purpose: renaming
    // either one in TinadecTools must fail desktop, not silently re-bucket every file.
    expect(labelOf(' ')).toBe('clean')
    expect(labelOf('?')).toBe('untracked')
  })

  it.each(CASES)(
    'porcelain %s%s puts the row in the right sections',
    (staged, unstaged, expectStaged, expectUnstaged) => {
      const file = entry(staged, unstaged)
      expect(isStagedFile(file), `staged section for ${file.staged_status}/${file.unstaged_status}`).toBe(expectStaged)
      expect(isUnstagedFile(file), `changes section for ${file.staged_status}/${file.unstaged_status}`).toBe(expectUnstaged)
    },
  )

  it('never leaves a reported row outside both sections', () => {
    const codes = [...new Set(MARKS.map((mark) => mark.code))]
    const orphans = []
    for (const staged of codes) {
      for (const unstaged of codes) {
        const file = entry(staged, unstaged)
        if (staged === ' ' && unstaged === ' ') continue
        if (!isStagedFile(file) && !isUnstagedFile(file)) orphans.push(`${staged}${unstaged} → ${file.status}`)
      }
    }
    expect(orphans).toEqual([])
  })

  it('falls back to the summary column when a payload carries no sides', () => {
    expect(isUnstagedFile({ status: 'modified' })).toBe(true)
    expect(isUnstagedFile({ status: 'untracked' })).toBe(true)
    expect(isStagedFile({ status: 'staged_added' })).toBe(true)
    expect(isStagedFile({ status: 'modified' })).toBe(false)
    expect(isStagedFile({ status: 'clean' })).toBe(false)
  })
})
