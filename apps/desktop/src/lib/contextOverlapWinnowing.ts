/**
 * 64-character rolling-hash Winnowing context overlap auditor.
 *
 * Implements Schleimer, Wilkerson & Aiken (2003) Winnowing fingerprinting over
 * whitespace-normalized text while preserving exact 1-based original line ranges
 * (`L1 <-> L2`), deduplicated shared character counts, token waste estimates,
 * and source-aware remediation suggestions.
 */

export type ContextSourceKind =
  | 'system_prompt'
  | 'pipeline_template'
  | 'workspace_instructions'
  | 'tool_schema'
  | 'skill'
  | 'prompt_fragment'
  | 'other'

export interface ContextAuditSource {
  id: string
  label: string
  kind: ContextSourceKind
  content: string
  path?: string | null
  meta?: string | null
}

export interface LineRange {
  start: number
  end: number
}

export type OverlapRemediationCode =
  | 'move_to_skill_references'
  | 'dedupe_workspace_instructions'
  | 'dedupe_tool_schema_prose'
  | 'consolidate_shared_skill'
  | 'extract_shared_fragment'

export interface OverlapPassageMatch {
  id: string
  sourceAId: string
  sourceALabel: string
  sourceAKind: ContextSourceKind
  sourceBId: string
  sourceBLabel: string
  sourceBKind: ContextSourceKind
  sourceALines: LineRange
  sourceBLines: LineRange
  sharedChars: number
  estimatedDuplicateTokens: number
  excerpt: string
  remediationCode: OverlapRemediationCode
  remediationSummary: string
}

export interface ContextOverlapPairSummary {
  id: string
  sourceAId: string
  sourceALabel: string
  sourceAKind: ContextSourceKind
  sourceBId: string
  sourceBLabel: string
  sourceBKind: ContextSourceKind
  sharedChars: number
  overlapPercent: number
  overlapPercentA: number
  overlapPercentB: number
  estimatedDuplicateTokens: number
  matchCount: number
  matches: OverlapPassageMatch[]
  remediationCode: OverlapRemediationCode
  remediationSummary: string
}

export interface ContextOverlapSourceStats {
  id: string
  label: string
  kind: ContextSourceKind
  path?: string | null
  charCount: number
  lineCount: number
  estimatedTokens: number
  duplicatedChars: number
  overlapPercent: number
}

export interface ContextOverlapAuditResult {
  kGramSize: number
  windowSize: number
  sourceCount: number
  totalEstimatedTokens: number
  duplicateEstimatedTokens: number
  overallOverlapPercent: number
  pairs: ContextOverlapPairSummary[]
  matches: OverlapPassageMatch[]
  sources: ContextOverlapSourceStats[]
}

export interface WinnowingOptions {
  /** k-gram length in normalized characters (default: 64). */
  kGramSize?: number
  /** Winnowing selection window width (default: 16). */
  windowSize?: number
  /** Maximum excerpt length in characters (default: 180). */
  maxExcerptLength?: number
  /** Maximum passage matches returned per source pair (default: 12). */
  maxMatchesPerPair?: number
}

export interface WinnowFingerprint {
  hash: number
  pos: number
}

interface NormalizedDocument {
  source: ContextAuditSource
  normalized: string
  /** Maps normalized char index -> raw char offset in `source.content`. */
  origOffsets: number[]
  /** Raw char offsets where each 1-based line begins. */
  lineStarts: number[]
  lineCount: number
  estimatedTokens: number
}

interface SpanPair {
  startA: number
  endA: number
  startB: number
  endB: number
}

// Dual 26-bit prime moduli: M1 * M2 ≈ 4.50e15 < Number.MAX_SAFE_INTEGER (9.007e15),
// and M2 * B2 < 9.2e9 << Number.MAX_SAFE_INTEGER, so combined 52-bit hash is exact.
const BASE_1 = 131
const MOD_1 = 67108859
const BASE_2 = 137
const MOD_2 = 67108879

const DEFAULT_K = 64
const DEFAULT_W = 16
const DEFAULT_MAX_EXCERPT = 180
const DEFAULT_MAX_MATCHES_PER_PAIR = 12

const CJK_CHAR_RE = /[\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\uac00-\ud7af]/

/** CJK-aware lightweight token estimator (~1.5 CJK chars/token, ~4 Latin chars/token). */
export function estimateTokens(text: string): number {
  if (!text) return 0
  let cjk = 0
  let nonCjk = 0
  for (let i = 0; i < text.length; i++) {
    const ch = text[i]!
    if (CJK_CHAR_RE.test(ch)) cjk++
    else if (!/\s/.test(ch)) nonCjk++
    else nonCjk += 0.25
  }
  const tokens = cjk / 1.5 + nonCjk / 4
  return Math.max(1, Math.ceil(tokens))
}

export function formatLineRange(range: LineRange): string {
  return range.start === range.end ? `L${range.start}` : `L${range.start}-L${range.end}`
}

export function formatLineRangePair(a: LineRange, b: LineRange): string {
  return `${formatLineRange(a)} <-> ${formatLineRange(b)}`
}

function buildLineStarts(content: string): number[] {
  const starts = [0]
  for (let i = 0; i < content.length; i++) {
    if (content.charCodeAt(i) === 10) {
      starts.push(i + 1)
    }
  }
  return starts
}

function offsetToLine(lineStarts: number[], offset: number): number {
  let lo = 0
  let hi = lineStarts.length - 1
  while (lo <= hi) {
    const mid = (lo + hi) >> 1
    if (lineStarts[mid]! <= offset) lo = mid + 1
    else hi = mid - 1
  }
  return Math.max(1, hi + 1)
}

function normalizeWithOffsets(source: ContextAuditSource): NormalizedDocument {
  const raw = source.content ?? ''
  const chars: string[] = []
  const origOffsets: number[] = []
  let prevWasSpace = true

  for (let i = 0; i < raw.length; i++) {
    const ch = raw[i]!
    const code = raw.charCodeAt(i)
    const isSpace = code <= 32 || /\s/.test(ch)
    if (isSpace) {
      if (!prevWasSpace) {
        chars.push(' ')
        origOffsets.push(i)
        prevWasSpace = true
      }
      continue
    }
    prevWasSpace = false
    const lower = code >= 65 && code <= 90 ? String.fromCharCode(code + 32) : ch
    chars.push(lower)
    origOffsets.push(i)
  }

  if (chars.length > 0 && chars[chars.length - 1] === ' ') {
    chars.pop()
    origOffsets.pop()
  }

  const lineStarts = buildLineStarts(raw)
  return {
    source,
    normalized: chars.join(''),
    origOffsets,
    lineStarts,
    lineCount: raw.length === 0 ? 0 : lineStarts.length,
    estimatedTokens: raw.trim().length === 0 ? 0 : estimateTokens(raw),
  }
}

/**
 * Compute genuine rolling-hash Winnowing fingerprints (`k`-grams over window `w`).
 *
 * Uses Rabin-Karp dual-prime rolling hash in O(n) time and monotonic deque
 * window selection in O(n) time with rightmost tie-breaking.
 */
export function winnowFingerprints(
  normalizedText: string,
  kGramSize = DEFAULT_K,
  windowSize = DEFAULT_W,
): WinnowFingerprint[] {
  const k = Math.max(1, Math.floor(kGramSize))
  const w = Math.max(1, Math.floor(windowSize))
  const n = normalizedText.length
  if (n < k) return []

  const hashCount = n - k + 1
  const hashes = new Float64Array(hashCount)

  let pow1 = 1
  let pow2 = 1
  let h1 = 0
  let h2 = 0

  for (let i = 0; i < k; i++) {
    const c = normalizedText.charCodeAt(i) + 1
    h1 = (h1 * BASE_1 + c) % MOD_1
    h2 = (h2 * BASE_2 + c) % MOD_2
    if (i > 0) {
      pow1 = (pow1 * BASE_1) % MOD_1
      pow2 = (pow2 * BASE_2) % MOD_2
    }
  }
  hashes[0] = h1 * MOD_2 + h2

  for (let i = k; i < n; i++) {
    const outCode = normalizedText.charCodeAt(i - k) + 1
    const inCode = normalizedText.charCodeAt(i) + 1

    h1 = (h1 - ((outCode * pow1) % MOD_1) + MOD_1) % MOD_1
    h1 = (h1 * BASE_1 + inCode) % MOD_1

    h2 = (h2 - ((outCode * pow2) % MOD_2) + MOD_2) % MOD_2
    h2 = (h2 * BASE_2 + inCode) % MOD_2

    hashes[i - k + 1] = h1 * MOD_2 + h2
  }

  const effectiveWindow = Math.min(w, hashCount)
  const fingerprints: WinnowFingerprint[] = []

  // Monotonic queue storing indices with non-decreasing hash values.
  // Use `>=` on back pop so equal hashes favor the rightmost position.
  const deque: number[] = []
  let head = 0
  let lastRecordedPos = -1

  for (let i = 0; i < hashCount; i++) {
    const curHash = hashes[i]!
    while (deque.length > head && hashes[deque[deque.length - 1]!]! >= curHash) {
      deque.pop()
    }
    deque.push(i)

    const winStart = i - effectiveWindow + 1
    if (deque[head]! < winStart) {
      head++
    }

    if (i >= effectiveWindow - 1) {
      const minPos = deque[head]!
      if (minPos !== lastRecordedPos) {
        fingerprints.push({ hash: hashes[minPos]!, pos: minPos })
        lastRecordedPos = minPos
      }
    }
  }

  return fingerprints
}

function mergeSpanPairs(spans: SpanPair[]): SpanPair[] {
  if (spans.length <= 1) return spans
  const sorted = [...spans].sort(
    (x, y) => x.startA - y.startA || x.startB - y.startB || (y.endA - y.startA) - (x.endA - x.startA),
  )
  const merged: SpanPair[] = []
  for (const span of sorted) {
    let absorbed = false
    for (let i = merged.length - 1; i >= 0; i--) {
      const existing = merged[i]!
      const overlapA = span.startA <= existing.endA && existing.startA <= span.endA
      const overlapB = span.startB <= existing.endB && existing.startB <= span.endB
      if (overlapA && overlapB) {
        existing.startA = Math.min(existing.startA, span.startA)
        existing.endA = Math.max(existing.endA, span.endA)
        existing.startB = Math.min(existing.startB, span.startB)
        existing.endB = Math.max(existing.endB, span.endB)
        absorbed = true
        break
      }
    }
    if (!absorbed) merged.push({ ...span })
  }
  return merged
}

function mergedIntervalLength(intervals: Array<[number, number]>): number {
  if (intervals.length === 0) return 0
  const sorted = [...intervals].sort((a, b) => a[0] - b[0] || a[1] - b[1])
  let total = 0
  let curStart = sorted[0]![0]
  let curEnd = sorted[0]![1]
  for (let i = 1; i < sorted.length; i++) {
    const [s, e] = sorted[i]!
    if (s <= curEnd) {
      if (e > curEnd) curEnd = e
    } else {
      total += Math.max(0, curEnd - curStart)
      curStart = s
      curEnd = e
    }
  }
  total += Math.max(0, curEnd - curStart)
  return total
}

export function resolveOverlapRemediation(
  kindA: ContextSourceKind,
  kindB: ContextSourceKind,
): { code: OverlapRemediationCode; summary: string } {
  const pair = new Set<ContextSourceKind>([kindA, kindB])

  if (
    pair.has('skill')
    && (pair.has('system_prompt') || pair.has('pipeline_template') || pair.has('prompt_fragment'))
  ) {
    return {
      code: 'move_to_skill_references',
      summary:
        'Move repeated procedural rules into on-demand SKILL.md or references/ and keep only a one-line trigger pointer in the always-on prompt.',
    }
  }

  if (pair.has('workspace_instructions')) {
    return {
      code: 'dedupe_workspace_instructions',
      summary:
        'Keep repository conventions in a single AGENTS.md or .apm/instructions file and remove duplicated policy prose from prompts or skills.',
    }
  }

  if (pair.has('tool_schema')) {
    return {
      code: 'dedupe_tool_schema_prose',
      summary:
        'Keep parameter and usage constraints inside tool schema descriptions instead of repeating them across system prompts or skills.',
    }
  }

  if (kindA === 'skill' && kindB === 'skill') {
    return {
      code: 'consolidate_shared_skill',
      summary:
        'Extract duplicated workflow checklists into a shared references/ document or consolidate overlapping skills.',
    }
  }

  return {
    code: 'extract_shared_fragment',
    summary:
      'Consolidate duplicated instructions into a single reusable prompt fragment or on-demand skill reference to stop paying twice every turn.',
  }
}

function buildExcerpt(doc: NormalizedDocument, normStart: number, normEnd: number, maxLen: number): string {
  if (doc.origOffsets.length === 0 || normStart >= normEnd) return ''
  const rawStart = doc.origOffsets[Math.max(0, Math.min(normStart, doc.origOffsets.length - 1))] ?? 0
  const rawEnd = (doc.origOffsets[Math.max(0, Math.min(normEnd - 1, doc.origOffsets.length - 1))] ?? rawStart) + 1
  const rawSlice = doc.source.content.slice(rawStart, rawEnd).replace(/\s+/g, ' ').trim()
  if (rawSlice.length <= maxLen) return rawSlice
  return `${rawSlice.slice(0, Math.max(16, maxLen - 1)).trimEnd()}…`
}

function normSpanToLines(doc: NormalizedDocument, normStart: number, normEnd: number): LineRange {
  if (doc.origOffsets.length === 0 || doc.lineCount === 0) return { start: 1, end: 1 }
  const clampedStart = Math.max(0, Math.min(normStart, doc.origOffsets.length - 1))
  const clampedEnd = Math.max(clampedStart, Math.min(normEnd - 1, doc.origOffsets.length - 1))
  const rawStart = doc.origOffsets[clampedStart] ?? 0
  const rawEnd = doc.origOffsets[clampedEnd] ?? rawStart
  return {
    start: offsetToLine(doc.lineStarts, rawStart),
    end: offsetToLine(doc.lineStarts, rawEnd),
  }
}

/**
 * Audit a collection of context sources (`system_prompt`, `pipeline_template`,
 * `workspace_instructions`, `tool_schema`, `skill`, `prompt_fragment`) for
 * cross-source textual overlap using 64-char rolling-hash Winnowing.
 */
export function auditContextOverlap(
  sources: ContextAuditSource[],
  options: WinnowingOptions = {},
): ContextOverlapAuditResult {
  const k = Math.max(1, Math.floor(options.kGramSize ?? DEFAULT_K))
  const w = Math.max(1, Math.floor(options.windowSize ?? DEFAULT_W))
  const maxExcerpt = options.maxExcerptLength ?? DEFAULT_MAX_EXCERPT
  const maxMatchesPerPair = options.maxMatchesPerPair ?? DEFAULT_MAX_MATCHES_PER_PAIR

  const docs = sources.map(normalizeWithOffsets)
  const index = new Map<number, Array<{ docIdx: number; pos: number }>>()

  for (let docIdx = 0; docIdx < docs.length; docIdx++) {
    const doc = docs[docIdx]!
    const fps = winnowFingerprints(doc.normalized, k, w)
    for (const fp of fps) {
      let bucket = index.get(fp.hash)
      if (!bucket) {
        bucket = []
        index.set(fp.hash, bucket)
      }
      bucket.push({ docIdx, pos: fp.pos })
    }
  }

  // Pair key -> candidate raw expanded spans
  const pairSpans = new Map<string, { idxA: number; idxB: number; spans: SpanPair[] }>()
  // Guard against degenerate pathological buckets (e.g., 50 identical boilerplate docs)
  const MAX_BUCKET_POSTINGS = 64

  for (const postings of index.values()) {
    if (postings.length < 2) continue
    const capped = postings.length > MAX_BUCKET_POSTINGS ? postings.slice(0, MAX_BUCKET_POSTINGS) : postings

    for (let i = 0; i < capped.length; i++) {
      const p1 = capped[i]!
      for (let j = i + 1; j < capped.length; j++) {
        const p2 = capped[j]!
        if (p1.docIdx === p2.docIdx) continue

        const idxA = p1.docIdx < p2.docIdx ? p1.docIdx : p2.docIdx
        const posA = p1.docIdx < p2.docIdx ? p1.pos : p2.pos
        const idxB = p1.docIdx < p2.docIdx ? p2.docIdx : p1.docIdx
        const posB = p1.docIdx < p2.docIdx ? p2.pos : p1.pos

        const textA = docs[idxA]!.normalized
        const textB = docs[idxB]!.normalized

        // Exact collision verification for the k-gram before extending.
        if (textA.slice(posA, posA + k) !== textB.slice(posB, posB + k)) {
          continue
        }

        let startA = posA
        let startB = posB
        while (startA > 0 && startB > 0 && textA.charCodeAt(startA - 1) === textB.charCodeAt(startB - 1)) {
          startA--
          startB--
        }

        let endA = posA + k
        let endB = posB + k
        while (endA < textA.length && endB < textB.length && textA.charCodeAt(endA) === textB.charCodeAt(endB)) {
          endA++
          endB++
        }

        // Trim boundary whitespace (often originating from line breaks before/after the passage)
        // so 1-based line ranges point strictly to the matched content lines.
        while (startA < endA && startB < endB && textA.charCodeAt(startA) === 32 && textB.charCodeAt(startB) === 32) {
          startA++
          startB++
        }
        while (endA > startA && endB > startB && textA.charCodeAt(endA - 1) === 32 && textB.charCodeAt(endB - 1) === 32) {
          endA--
          endB--
        }

        if (endA - startA < k || endB - startB < k) {
          continue
        }

        const key = `${idxA}::${idxB}`
        let entry = pairSpans.get(key)
        if (!entry) {
          entry = { idxA, idxB, spans: [] }
          pairSpans.set(key, entry)
        }
        entry.spans.push({ startA, endA, startB, endB })
      }
    }
  }

  const perSourceDupIntervals: Array<Array<[number, number]>> = docs.map(() => [])
  const pairs: ContextOverlapPairSummary[] = []
  const allMatches: OverlapPassageMatch[] = []

  for (const { idxA, idxB, spans } of pairSpans.values()) {
    const docA = docs[idxA]!
    const docB = docs[idxB]!
    const merged = mergeSpanPairs(spans)
      .filter((s) => s.endA - s.startA >= k && s.endB - s.startB >= k)
      .sort((a, b) => (b.endA - b.startA) - (a.endA - a.startA) || a.startA - b.startA)

    if (merged.length === 0) continue

    const intervalsA: Array<[number, number]> = []
    const intervalsB: Array<[number, number]> = []
    for (const s of merged) {
      intervalsA.push([s.startA, s.endA])
      intervalsB.push([s.startB, s.endB])
      perSourceDupIntervals[idxA]!.push([s.startA, s.endA])
      perSourceDupIntervals[idxB]!.push([s.startB, s.endB])
    }

    const sharedCharsA = mergedIntervalLength(intervalsA)
    const sharedCharsB = mergedIntervalLength(intervalsB)
    const sharedChars = Math.min(sharedCharsA, sharedCharsB)
    const minLen = Math.max(1, Math.min(docA.normalized.length, docB.normalized.length))
    const overlapPercent = Math.min(100, Math.round((sharedChars / minLen) * 1000) / 10)
    const overlapPercentA = docA.normalized.length > 0
      ? Math.min(100, Math.round((sharedCharsA / docA.normalized.length) * 1000) / 10)
      : 0
    const overlapPercentB = docB.normalized.length > 0
      ? Math.min(100, Math.round((sharedCharsB / docB.normalized.length) * 1000) / 10)
      : 0

    const remediation = resolveOverlapRemediation(docA.source.kind, docB.source.kind)
    const pairId = `${docA.source.id}__${docB.source.id}`

    const pairMatches: OverlapPassageMatch[] = merged.slice(0, maxMatchesPerPair).map((span, idx) => {
      const sliceText = docA.normalized.slice(span.startA, span.endA)
      const dupTokens = estimateTokens(sliceText)
      return {
        id: `${pairId}#${idx + 1}`,
        sourceAId: docA.source.id,
        sourceALabel: docA.source.label,
        sourceAKind: docA.source.kind,
        sourceBId: docB.source.id,
        sourceBLabel: docB.source.label,
        sourceBKind: docB.source.kind,
        sourceALines: normSpanToLines(docA, span.startA, span.endA),
        sourceBLines: normSpanToLines(docB, span.startB, span.endB),
        sharedChars: span.endA - span.startA,
        estimatedDuplicateTokens: dupTokens,
        excerpt: buildExcerpt(docA, span.startA, span.endA, maxExcerpt),
        remediationCode: remediation.code,
        remediationSummary: remediation.summary,
      }
    })

    // Estimate pair duplicate tokens from unique matched text (never double-counted).
    const pairDuplicateTokens = Math.max(
      1,
      Math.round(
        (sharedChars / Math.max(1, docA.normalized.length)) * Math.max(1, docA.estimatedTokens),
      ),
    )

    pairs.push({
      id: pairId,
      sourceAId: docA.source.id,
      sourceALabel: docA.source.label,
      sourceAKind: docA.source.kind,
      sourceBId: docB.source.id,
      sourceBLabel: docB.source.label,
      sourceBKind: docB.source.kind,
      sharedChars,
      overlapPercent,
      overlapPercentA,
      overlapPercentB,
      estimatedDuplicateTokens: pairDuplicateTokens,
      matchCount: merged.length,
      matches: pairMatches,
      remediationCode: remediation.code,
      remediationSummary: remediation.summary,
    })

    allMatches.push(...pairMatches)
  }

  pairs.sort(
    (a, b) =>
      b.estimatedDuplicateTokens - a.estimatedDuplicateTokens
      || b.sharedChars - a.sharedChars
      || a.id.localeCompare(b.id),
  )
  allMatches.sort(
    (a, b) =>
      b.sharedChars - a.sharedChars
      || b.estimatedDuplicateTokens - a.estimatedDuplicateTokens
      || a.id.localeCompare(b.id),
  )

  let totalEstimatedTokens = 0
  let totalChars = 0
  let totalDuplicatedChars = 0

  const sourceStats: ContextOverlapSourceStats[] = docs.map((doc, idx) => {
    const dupChars = mergedIntervalLength(perSourceDupIntervals[idx]!)
    const normLen = doc.normalized.length
    totalEstimatedTokens += doc.estimatedTokens
    totalChars += normLen
    totalDuplicatedChars += dupChars
    return {
      id: doc.source.id,
      label: doc.source.label,
      kind: doc.source.kind,
      path: doc.source.path ?? null,
      charCount: doc.source.content.length,
      lineCount: doc.lineCount,
      estimatedTokens: doc.estimatedTokens,
      duplicatedChars: dupChars,
      overlapPercent: normLen > 0 ? Math.min(100, Math.round((dupChars / normLen) * 1000) / 10) : 0,
    }
  })

  // Half of per-source duplicated chars represents redundant copy beyond a single canonical owner.
  const redundantChars = Math.round(totalDuplicatedChars / 2)
  const duplicateEstimatedTokens =
    totalChars > 0 && redundantChars > 0
      ? Math.max(1, Math.round((redundantChars / totalChars) * totalEstimatedTokens))
      : 0
  const overallOverlapPercent =
    totalEstimatedTokens > 0
      ? Math.min(100, Math.round((duplicateEstimatedTokens / totalEstimatedTokens) * 1000) / 10)
      : 0

  return {
    kGramSize: k,
    windowSize: w,
    sourceCount: docs.length,
    totalEstimatedTokens,
    duplicateEstimatedTokens,
    overallOverlapPercent,
    pairs,
    matches: allMatches,
    sources: sourceStats,
  }
}
