import { describe, expect, it } from 'vitest'
import { buildGitIndexPatch, changeBlockLineIds } from './gitIndexPatch'
import { parseUnifiedDiff } from './gitDiffParser'

const source = `diff --git a/note.txt b/note.txt
index 1111111..2222222 100644
--- a/note.txt
+++ b/note.txt
@@ -1,3 +1,3 @@
 one
-two
+TWO
 three
`

describe('gitIndexPatch', () => {
  it('keeps a replacement block atomic and builds an applicable unified hunk', () => {
    const parsed = parseUnifiedDiff(source)
    const hunk = parsed.files[0]!.hunks[0]!
    const deleteLine = hunk.lines.find((line) => line.change === 'delete')!
    const ids = new Set(changeBlockLineIds(hunk, deleteLine.id))

    expect(ids.size).toBe(2)
    expect(buildGitIndexPatch(parsed, ids)).toBe(`diff --git a/note.txt b/note.txt
--- a/note.txt
+++ b/note.txt
@@ -1,3 +1,3 @@
 one
-two
+TWO
 three
`)
  })
})
