import { describe, expect, it } from 'vitest'
import {
  FRACTIONAL_KEY_ALPHABET,
  compareFractionalKeys,
  generateKeyBetween,
  generateNKeysBetween,
  isValidFractionalKey,
} from './spatialFractionalIndex'

describe('spatialFractionalIndex', () => {
  it('uses a strictly code-point ordered 62-character Base-62 alphabet', () => {
    expect(FRACTIONAL_KEY_ALPHABET).toHaveLength(62)
    for (let i = 1; i < FRACTIONAL_KEY_ALPHABET.length; i++) {
      expect(FRACTIONAL_KEY_ALPHABET.charCodeAt(i - 1)).toBeLessThan(
        FRACTIONAL_KEY_ALPHABET.charCodeAt(i),
      )
      expect(compareFractionalKeys(FRACTIONAL_KEY_ALPHABET[i - 1], FRACTIONAL_KEY_ALPHABET[i])).toBe(-1)
    }
    expect(compareFractionalKeys('V', 'V')).toBe(0)
    expect(compareFractionalKeys('a', 'Z')).toBe(1)
    expect(isValidFractionalKey('09AZaz')).toBe(true)
    expect(isValidFractionalKey('')).toBe(false)
    expect(isValidFractionalKey('a-b')).toBe(false)
  })

  it('generates keys across open bounds, adjacent digits, and prefix intervals', () => {
    const first = generateKeyBetween(null, null)
    expect(first).toBe('V')

    const beforeFirst = generateKeyBetween(null, first)
    const afterFirst = generateKeyBetween(first, null)
    expect(compareFractionalKeys(beforeFirst, first)).toBe(-1)
    expect(compareFractionalKeys(first, afterFirst)).toBe(-1)

    const pairs: Array<[string | null, string | null]> = [
      ['0', '1'],
      ['9', 'A'],
      ['Z', 'a'],
      ['V', 'W'],
      ['V', 'VV'],
      ['a', 'a01'],
      ['a0', 'a1'],
      ['zz', null],
      [null, '1'],
      [null, '01'],
    ]

    for (const [lo, hi] of pairs) {
      const mid = generateKeyBetween(lo, hi)
      expect(isValidFractionalKey(mid)).toBe(true)
      expect(mid.endsWith('0')).toBe(false)
      if (lo !== null) expect(compareFractionalKeys(lo, mid)).toBe(-1)
      if (hi !== null) expect(compareFractionalKeys(mid, hi)).toBe(-1)
    }
  })

  it('strictly preserves ordering across 1,200+ consecutive midpoint insertions (left, right, and alternating)', () => {
    // 1. 1,200 consecutive left-leaning midpoint insertions
    let upper = generateKeyBetween(null, null)
    const lowerFixed = generateKeyBetween(null, upper)
    for (let i = 0; i < 1200; i++) {
      const mid = generateKeyBetween(lowerFixed, upper)
      expect(compareFractionalKeys(lowerFixed, mid)).toBe(-1)
      expect(compareFractionalKeys(mid, upper)).toBe(-1)
      upper = mid
    }
    expect(upper.length).toBeLessThan(260)

    // 2. 1,200 consecutive right-leaning midpoint insertions
    let lower = 'V'
    const upperFixed = 'W'
    for (let i = 0; i < 1200; i++) {
      const mid = generateKeyBetween(lower, upperFixed)
      expect(compareFractionalKeys(lower, mid)).toBe(-1)
      expect(compareFractionalKeys(mid, upperFixed)).toBe(-1)
      lower = mid
    }
    expect(lower.length).toBeLessThan(260)

    // 3. 1,200 alternating interior midpoint insertions
    let lo = 'A'
    let hi = 'B'
    for (let i = 0; i < 1200; i++) {
      const mid = generateKeyBetween(lo, hi)
      expect(compareFractionalKeys(lo, mid)).toBe(-1)
      expect(compareFractionalKeys(mid, hi)).toBe(-1)
      if (i % 2 === 0) lo = mid
      else hi = mid
    }
    expect(Math.max(lo.length, hi.length)).toBeLessThan(260)
  })

  it('strictly preserves ordering across 1,000 consecutive prepends and 1,000 consecutive appends', () => {
    const keys: string[] = [generateKeyBetween(null, null)]
    let head = keys[0]
    for (let i = 0; i < 1000; i++) {
      const prev = generateKeyBetween(null, head)
      expect(compareFractionalKeys(prev, head)).toBe(-1)
      head = prev
    }

    let tail = keys[0]
    for (let i = 0; i < 1000; i++) {
      const next = generateKeyBetween(tail, null)
      expect(compareFractionalKeys(tail, next)).toBe(-1)
      tail = next
    }
  })

  it('generates balanced, compact N keys between any valid bounds', () => {
    expect(generateNKeysBetween(null, null, 0)).toEqual([])
    expect(generateNKeysBetween('A', 'z', 1)).toHaveLength(1)

    const open500 = generateNKeysBetween(null, null, 500)
    expect(open500).toHaveLength(500)
    for (let i = 1; i < open500.length; i++) {
      expect(compareFractionalKeys(open500[i - 1], open500[i])).toBe(-1)
      expect(open500[i].length).toBeLessThanOrEqual(4)
    }

    const bounded200 = generateNKeysBetween('M', 'N', 200)
    expect(bounded200).toHaveLength(200)
    expect(compareFractionalKeys('M', bounded200[0])).toBe(-1)
    expect(compareFractionalKeys(bounded200[bounded200.length - 1], 'N')).toBe(-1)
    for (let i = 1; i < bounded200.length; i++) {
      expect(compareFractionalKeys(bounded200[i - 1], bounded200[i])).toBe(-1)
    }
  })

  it('rejects reversed bounds, malformed characters, negative counts, and impossible zero-only upper suffixes', () => {
    expect(() => generateKeyBetween('b', 'a')).toThrow(RangeError)
    expect(() => generateKeyBetween('a', 'a')).toThrow(RangeError)
    expect(() => generateKeyBetween('', 'a')).toThrow(RangeError)
    expect(() => generateKeyBetween('a!', 'b')).toThrow(RangeError)
    expect(() => generateKeyBetween(null, '0')).toThrow(RangeError)
    expect(() => generateKeyBetween('a', 'a00')).toThrow(RangeError)
    expect(() => generateNKeysBetween(null, null, -1)).toThrow(RangeError)
    expect(() => generateNKeysBetween(null, null, 1.5)).toThrow(RangeError)
  })
})
