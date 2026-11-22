// Pure Base-62 arbitrary-precision fractional indexing (Figma-style string sort keys).
//
// Alphabet is strictly ordered by ASCII / UTF-16 code point:
//   '0'..'9' (0..9) < 'A'..'Z' (10..35) < 'a'..'z' (36..61)
// Therefore standard lexicographical comparison (`a < b ? -1 : a > b ? 1 : 0`)
// coincides with numerical order over base-62 fractions in (0, 1).

export const FRACTIONAL_KEY_ALPHABET = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz'
const BASE = FRACTIONAL_KEY_ALPHABET.length // 62
const MID_CHAR = FRACTIONAL_KEY_ALPHABET[Math.floor(BASE / 2)] // 'V' (31)

const CHAR_TO_DIGIT = (() => {
  const table = new Int8Array(128).fill(-1)
  for (let i = 0; i < BASE; i++) {
    table[FRACTIONAL_KEY_ALPHABET.charCodeAt(i)] = i
  }
  return table
})()

function digitAt(key: string, index: number): number {
  const code = key.charCodeAt(index)
  return code < 128 ? CHAR_TO_DIGIT[code] : -1
}

export function isValidFractionalKey(key: string): boolean {
  if (typeof key !== 'string' || key.length === 0) return false
  for (let i = 0; i < key.length; i++) {
    if (digitAt(key, i) < 0) return false
  }
  return true
}

function assertValidKey(key: string, label: string): void {
  if (!isValidFractionalKey(key)) {
    throw new RangeError(`Invalid fractional key for ${label}: ${JSON.stringify(key)}`)
  }
}

/**
 * Lexicographical comparison over Base-62 fractional keys.
 * Returns -1 when `a < b`, 1 when `a > b`, and 0 when `a === b`.
 */
export function compareFractionalKeys(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0
}

function keyAbove(lo: string | null): string {
  if (lo === null) return MID_CHAR
  for (let i = 0; i < lo.length; i++) {
    const d = digitAt(lo, i)
    if (d < BASE - 1) {
      const mid = Math.floor((d + BASE) / 2)
      return lo.slice(0, i) + FRACTIONAL_KEY_ALPHABET[mid]
    }
  }
  return lo + MID_CHAR
}

function keyBelowSuffix(prefix: string, rem: string, origA: string | null, origB: string): string {
  for (let j = 0; j < rem.length; j++) {
    const d = digitAt(rem, j)
    if (d > 1) {
      const mid = Math.floor(d / 2)
      return prefix + '0'.repeat(j) + FRACTIONAL_KEY_ALPHABET[mid]
    }
    if (d === 1) {
      return prefix + '0'.repeat(j) + '0' + MID_CHAR
    }
  }
  throw new RangeError(
    `Cannot generate fractional key between ${JSON.stringify(origA)} and ${JSON.stringify(origB)}: upper bound has only trailing zeros`,
  )
}

/**
 * Generates a Base-62 fractional sort key strictly between `a` and `b`.
 * Preserves `compareFractionalKeys(a, key) < 0 && compareFractionalKeys(key, b) < 0`
 * across arbitrary-depth consecutive insertions without floating-point precision loss.
 */
export function generateKeyBetween(a?: string | null, b?: string | null): string {
  const lo = a === undefined || a === null ? null : a
  const hi = b === undefined || b === null ? null : b

  if (lo !== null) assertValidKey(lo, 'lower bound')
  if (hi !== null) assertValidKey(hi, 'upper bound')

  if (lo !== null && hi !== null && compareFractionalKeys(lo, hi) >= 0) {
    throw new RangeError(
      `Invalid fractional key interval: expected lower < upper, got ${JSON.stringify(lo)} >= ${JSON.stringify(hi)}`,
    )
  }

  if (hi === null) {
    return keyAbove(lo)
  }

  if (lo === null) {
    return keyBelowSuffix('', hi, null, hi)
  }

  let i = 0
  while (i < lo.length && lo.charCodeAt(i) === hi.charCodeAt(i)) {
    i++
  }

  // `lo` is a strict prefix of `hi`
  if (i === lo.length) {
    return keyBelowSuffix(lo, hi.slice(i), lo, hi)
  }

  const dLo = digitAt(lo, i)
  const dHi = digitAt(hi, i)
  if (dHi - dLo > 1) {
    const mid = Math.floor((dLo + dHi) / 2)
    return lo.slice(0, i) + FRACTIONAL_KEY_ALPHABET[mid]
  }

  // Adjacent digits at first point of difference (`dHi - dLo === 1`):
  // keep `lo[0..i]` and pick any valid suffix strictly above `lo.slice(i + 1)`.
  const tail = i + 1 < lo.length ? lo.slice(i + 1) : null
  return lo.slice(0, i + 1) + keyAbove(tail)
}

function buildBalancedKeys(a: string | null, b: string | null, count: number, out: string[]): void {
  if (count <= 0) return
  if (count === 1) {
    out.push(generateKeyBetween(a, b))
    return
  }
  const midIndex = Math.floor(count / 2)
  const midKey = generateKeyBetween(a, b)
  buildBalancedKeys(a, midKey, midIndex, out)
  out.push(midKey)
  buildBalancedKeys(midKey, b, count - midIndex - 1, out)
}

/**
 * Generates `n` strictly ordered, balanced Base-62 fractional sort keys between `a` and `b`.
 */
export function generateNKeysBetween(
  a: string | null | undefined,
  b: string | null | undefined,
  n: number,
): string[] {
  if (!Number.isInteger(n) || n < 0) {
    throw new RangeError(`Expected non-negative integer count n, got ${n}`)
  }
  const lo = a === undefined || a === null ? null : a
  const hi = b === undefined || b === null ? null : b
  if (lo !== null) assertValidKey(lo, 'lower bound')
  if (hi !== null) assertValidKey(hi, 'upper bound')
  if (lo !== null && hi !== null && compareFractionalKeys(lo, hi) >= 0) {
    throw new RangeError(
      `Invalid fractional key interval: expected lower < upper, got ${JSON.stringify(lo)} >= ${JSON.stringify(hi)}`,
    )
  }
  if (n === 0) return []
  const out: string[] = []
  buildBalancedKeys(lo, hi, n, out)
  return out
}
