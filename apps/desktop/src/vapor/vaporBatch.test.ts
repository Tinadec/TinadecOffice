import { describe, it, expect } from 'vitest'
import { VAPOR_BATCHES, allVaporFiles, validateVaporBatches } from './vaporBatch'
import { VAPOR_EXEMPTIONS, VAPOR_OPTED_IN } from './VaporExemptions'
import { readFileSync, existsSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { resolve, dirname } from 'node:path'

// src/vapor/vaporBatch.test.ts -> apps/desktop/src
const srcRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')

function readSource(file: string): string | null {
  // batch files are listed as 'src/components/...' (relative to apps/desktop);
  // srcRoot is already apps/desktop/src, so strip a leading 'src/' when present.
  const rel = file.replace(/^src\//, '')
  const abs = resolve(srcRoot, rel)
  if (!existsSync(abs)) return null
  return readFileSync(abs, 'utf-8')
}

describe('vapor batch registry', () => {
  it('every batch has a unique id', () => {
    const ids = VAPOR_BATCHES.map((b) => b.id)
    expect(new Set(ids).size).toBe(ids.length)
  })

  it('every batch file exists on disk', () => {
    for (const file of allVaporFiles()) {
      expect(readSource(file), `missing ${file}`).not.toBeNull()
    }
  })

  it('every batch file actually carries the vapor block attribute', () => {
    const problems = validateVaporBatches(readSource)
    expect(problems).toEqual([])
  })

  it('exemption entries reference real files', () => {
    for (const e of VAPOR_EXEMPTIONS) {
      expect(readSource(e.file), `missing exemption file ${e.file}`).not.toBeNull()
    }
  })

  it('opted-in list is a subset of batch files (audit trail)', () => {
    const inBatches = new Set(allVaporFiles())
    for (const f of VAPOR_OPTED_IN) {
      expect(inBatches.has(f), `${f} not registered in any batch`).toBe(true)
    }
  })
})
