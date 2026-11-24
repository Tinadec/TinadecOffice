import type {
  ProjectBoardFiltersDto,
  ProjectBoardGroupBy,
  ProjectBoardLayoutMode,
  ProjectBoardSortBy,
  ProjectBoardSwimlaneBy,
  ProjectBoardVisibleField,
} from '@/api'

export type SpatialToolMode =
  | 'select'
  | 'pan'
  | 'section'
  | 'sticky'
  | 'text'
  | 'board'
  | 'pen'
  | 'highlighter'
  | 'eraser'
  | 'connector'
  | 'media'
  | 'comment'

export type SpatialStickyTone = 'butter' | 'mint' | 'sky' | 'lavender' | 'peach' | 'slate'

export const SPATIAL_STICKY_TONES: readonly SpatialStickyTone[] = [
  'butter',
  'mint',
  'sky',
  'lavender',
  'peach',
  'slate',
] as const

export interface SpatialStickyToneTokens {
  tone: SpatialStickyTone
  swatch: string
  bg: string
  border: string
  text: string
  muted: string
}

export const SPATIAL_STICKY_PALETTE: Record<SpatialStickyTone, SpatialStickyToneTokens> = {
  butter: {
    tone: 'butter',
    swatch: '#f6d56a',
    bg: 'color-mix(in srgb, #fef08a 82%, var(--surface-raised, #ffffff))',
    border: 'color-mix(in srgb, #eab308 34%, transparent)',
    text: '#29220a',
    muted: '#6b5c22',
  },
  mint: {
    tone: 'mint',
    swatch: '#78d9a8',
    bg: 'color-mix(in srgb, #bbf7d0 80%, var(--surface-raised, #ffffff))',
    border: 'color-mix(in srgb, #16a34a 32%, transparent)',
    text: '#0e2b1b',
    muted: '#336348',
  },
  sky: {
    tone: 'sky',
    swatch: '#7ec8f8',
    bg: 'color-mix(in srgb, #bae6fd 80%, var(--surface-raised, #ffffff))',
    border: 'color-mix(in srgb, #0284c7 32%, transparent)',
    text: '#0c2438',
    muted: '#325a78',
  },
  lavender: {
    tone: 'lavender',
    swatch: '#c4a8f8',
    bg: 'color-mix(in srgb, #e9d5ff 82%, var(--surface-raised, #ffffff))',
    border: 'color-mix(in srgb, #9333ea 30%, transparent)',
    text: '#231338',
    muted: '#5d417d',
  },
  peach: {
    tone: 'peach',
    swatch: '#f9b184',
    bg: 'color-mix(in srgb, #fed7aa 82%, var(--surface-raised, #ffffff))',
    border: 'color-mix(in srgb, #ea580c 30%, transparent)',
    text: '#331a09',
    muted: '#784828',
  },
  slate: {
    tone: 'slate',
    swatch: '#b8c0cc',
    bg: 'color-mix(in srgb, #e2e8f0 80%, var(--surface-raised, #ffffff))',
    border: 'color-mix(in srgb, #64748b 34%, transparent)',
    text: '#161c24',
    muted: '#4d5968',
  },
}

export type SpatialSectionTone = 'neutral' | 'indigo' | 'emerald' | 'amber' | 'rose' | 'sky'

export const SPATIAL_SECTION_TONES: readonly SpatialSectionTone[] = [
  'neutral',
  'indigo',
  'emerald',
  'amber',
  'rose',
  'sky',
] as const

export interface SpatialSectionToneTokens {
  tone: SpatialSectionTone
  swatch: string
  fill: string
  border: string
  headerBg: string
}

export const SPATIAL_SECTION_PALETTE: Record<SpatialSectionTone, SpatialSectionToneTokens> = {
  neutral: {
    tone: 'neutral',
    swatch: '#94a3b8',
    fill: 'color-mix(in srgb, var(--surface-section, rgba(148, 163, 184, 0.1)) 46%, transparent)',
    border: 'color-mix(in srgb, var(--border-default, #64748b) 72%, transparent)',
    headerBg: 'color-mix(in srgb, var(--surface-raised, #ffffff) 86%, transparent)',
  },
  indigo: {
    tone: 'indigo',
    swatch: '#818cf8',
    fill: 'color-mix(in srgb, #6366f1 8%, transparent)',
    border: 'color-mix(in srgb, #6366f1 36%, var(--border-muted, #64748b))',
    headerBg: 'color-mix(in srgb, #6366f1 14%, var(--surface-raised, #ffffff))',
  },
  emerald: {
    tone: 'emerald',
    swatch: '#34d399',
    fill: 'color-mix(in srgb, #10b981 8%, transparent)',
    border: 'color-mix(in srgb, #10b981 36%, var(--border-muted, #64748b))',
    headerBg: 'color-mix(in srgb, #10b981 14%, var(--surface-raised, #ffffff))',
  },
  amber: {
    tone: 'amber',
    swatch: '#fbbf24',
    fill: 'color-mix(in srgb, #f59e0b 8%, transparent)',
    border: 'color-mix(in srgb, #f59e0b 36%, var(--border-muted, #64748b))',
    headerBg: 'color-mix(in srgb, #f59e0b 14%, var(--surface-raised, #ffffff))',
  },
  rose: {
    tone: 'rose',
    swatch: '#fb7185',
    fill: 'color-mix(in srgb, #f43f5e 8%, transparent)',
    border: 'color-mix(in srgb, #f43f5e 36%, var(--border-muted, #64748b))',
    headerBg: 'color-mix(in srgb, #f43f5e 14%, var(--surface-raised, #ffffff))',
  },
  sky: {
    tone: 'sky',
    swatch: '#38bdf8',
    fill: 'color-mix(in srgb, #0ea5e9 8%, transparent)',
    border: 'color-mix(in srgb, #0ea5e9 36%, var(--border-muted, #64748b))',
    headerBg: 'color-mix(in srgb, #0ea5e9 14%, var(--surface-raised, #ffffff))',
  },
}

export type SpatialTextScale = 'sm' | 'md' | 'lg' | 'xl'

export const SPATIAL_TEXT_SCALES: readonly SpatialTextScale[] = ['sm', 'md', 'lg', 'xl'] as const

export interface SpatialSectionProps {
  title: string
  color?: SpatialSectionTone | string
  collapsed?: boolean
  runId?: string | null
  board_task_id?: string | null
  board_task_key?: string | null
}

export interface SpatialStickyProps {
  text: string
  tone: SpatialStickyTone
  authorName?: string
  authorKind?: 'human' | 'ai'
  board_task_id?: string | null
  board_task_key?: string | null
}

export interface SpatialTextProps {
  text: string
  scale: SpatialTextScale
  color?: string
  align?: 'left' | 'center' | 'right'
  board_task_id?: string | null
  board_task_key?: string | null
}

export interface SpatialBoardWidgetProps {
  project_id?: string | null
  title?: string
  layout?: ProjectBoardLayoutMode
  group_by?: ProjectBoardGroupBy
  swimlane_by?: ProjectBoardSwimlaneBy
  sort_by?: ProjectBoardSortBy
  filters?: ProjectBoardFiltersDto
  visible_fields?: ProjectBoardVisibleField[]
  compact?: boolean
  saved_view_id?: string | null
}

export type SpatialDrawingMode = 'pen' | 'highlighter'

export interface SpatialDrawingProps {
  points: [number, number][]
  color: string
  strokeWidth: number
  mode: SpatialDrawingMode
}

export type SpatialMediaKind = 'image' | 'file'

export interface SpatialMediaProps {
  mediaKind: SpatialMediaKind
  path: string
  url?: string
  title: string
  snippet?: string
  language?: string
}

export type SpatialConnectorCurve = 'smoothstep' | 'bezier'

export interface SpatialConnectorProps {
  sourceId: string
  targetId: string
  sourceHandle?: string | null
  targetHandle?: string | null
  label?: string
  curve: SpatialConnectorCurve
  color?: string
}

export interface SpatialCommentReply {
  id: string
  authorId: string
  authorName: string
  authorKind: 'human' | 'ai'
  body: string
  createdAtUnixMs: number
}

export interface SpatialCommentPin {
  id: string
  objectId?: string | null
  x: number
  y: number
  authorId: string
  authorName: string
  authorKind: 'human' | 'ai'
  body: string
  mentions: string[]
  replies: SpatialCommentReply[]
  resolved: boolean
  createdAtUnixMs: number
  updatedAtUnixMs: number
}

export interface SpatialViewport {
  x: number
  y: number
  zoom: number
}

export interface SpatialSelectionBounds {
  x: number
  y: number
  width: number
  height: number
}

export type SpatialAlignMode = 'left' | 'centerX' | 'right' | 'top' | 'centerY' | 'bottom'
export type SpatialDistributeAxis = 'horizontal' | 'vertical'
export type SpatialSyncStatus = 'live' | 'syncing' | 'local'

export interface SpatialPresenceUpdate {
  cursor?: { x: number; y: number } | null
  selection?: string[]
  viewport?: SpatialViewport | null
  activeTool?: SpatialToolMode | string
  followingClientId?: string | null
}

export const SPATIAL_WHITEBOARD_DEFAULTS = {
  section: { width: 520, height: 340, color: 'neutral' as SpatialSectionTone },
  sticky: { width: 224, height: 224, tone: 'butter' as SpatialStickyTone },
  text: { width: 280, height: 56, scale: 'md' as SpatialTextScale },
  board: { width: 780, height: 480, layout: 'kanban' as ProjectBoardLayoutMode },
  mediaImage: { width: 320, height: 240 },
  mediaFile: { width: 360, height: 260 },
  pen: { color: '#3b82f6', strokeWidth: 3 },
  highlighter: { color: '#facc15', strokeWidth: 18 },
} as const

const ALLOWED_BOARD_WIDGET_PROP_KEYS = new Set<string>([
  'project_id',
  'title',
  'layout',
  'group_by',
  'swimlane_by',
  'sort_by',
  'filters',
  'visible_fields',
  'compact',
  'saved_view_id',
])

/**
 * Strips any task/card payload fields from a `kind: 'board'` spatial object's LWW props.
 * Only view projection metadata is ever stored in the spatial document (zero task data copy).
 */
export function sanitizeSpatialBoardWidgetProps(
  raw: Record<string, unknown> | Partial<SpatialBoardWidgetProps> | null | undefined,
): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  if (!raw || typeof raw !== 'object') return out
  for (const [key, value] of Object.entries(raw)) {
    if (!ALLOWED_BOARD_WIDGET_PROP_KEYS.has(key)) continue
    if (value !== undefined) {
      out[key] = value
    }
  }
  return out
}

/**
 * Computes a centered viewport for focusing a spatial object by its world bounds,
 * falling back to `viewportHint` when node bounds are not yet materialized.
 */
export function computeSpatialFocusViewport(options: {
  bounds?: { x: number; y: number; width: number; height: number } | null
  viewportHint?: { x?: unknown; y?: unknown; zoom?: unknown } | Record<string, unknown> | null
  surfaceWidth?: number
  surfaceHeight?: number
  currentZoom?: number
}): SpatialViewport | null {
  const sw = options.surfaceWidth && options.surfaceWidth > 0 ? options.surfaceWidth : 1280
  const sh = options.surfaceHeight && options.surfaceHeight > 0 ? options.surfaceHeight : 800
  const hint = options.viewportHint && typeof options.viewportHint === 'object' ? options.viewportHint : null
  const hintZoom = hint && typeof hint.zoom === 'number' && Number.isFinite(hint.zoom) ? hint.zoom : null

  if (options.bounds) {
    const rawZoom = hintZoom ?? options.currentZoom ?? 1
    const zoom = Math.max(0.55, Math.min(1.25, rawZoom))
    const cx = options.bounds.x + options.bounds.width / 2
    const cy = options.bounds.y + options.bounds.height / 2
    return {
      x: Math.round(sw / 2 - cx * zoom),
      y: Math.round(sh / 2 - cy * zoom),
      zoom: Number(zoom.toFixed(3)),
    }
  }

  if (
    hint &&
    typeof hint.x === 'number' &&
    Number.isFinite(hint.x) &&
    typeof hint.y === 'number' &&
    Number.isFinite(hint.y)
  ) {
    const zoom = Math.max(0.2, Math.min(2, hintZoom ?? options.currentZoom ?? 1))
    return {
      x: hint.x,
      y: hint.y,
      zoom: Number(zoom.toFixed(3)),
    }
  }

  return null
}

export function isStickyTone(value: unknown): value is SpatialStickyTone {
  return typeof value === 'string' && (SPATIAL_STICKY_TONES as readonly string[]).includes(value)
}

export function isSectionTone(value: unknown): value is SpatialSectionTone {
  return typeof value === 'string' && (SPATIAL_SECTION_TONES as readonly string[]).includes(value)
}

export function isTextScale(value: unknown): value is SpatialTextScale {
  return typeof value === 'string' && (SPATIAL_TEXT_SCALES as readonly string[]).includes(value)
}

export function encodeMediaPathToken(filePath: string): string {
  const normalized = filePath.replace(/\\/g, '/')
  const bytes = new TextEncoder().encode(normalized)
  let binary = ''
  for (const byte of bytes) binary += String.fromCharCode(byte)
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

/**
 * Converts a raw sequence of world-space points into a smooth quadratic-Bezier
 * SVG path string (midpoint interpolation) so FigJam ink strokes feel fluid.
 */
export function buildSmoothStrokePath(points: ReadonlyArray<readonly [number, number]>): string {
  if (!points.length) return ''
  if (points.length === 1) {
    const [x, y] = points[0]!
    return `M ${x.toFixed(2)} ${y.toFixed(2)} L ${(x + 0.1).toFixed(2)} ${(y + 0.1).toFixed(2)}`
  }
  if (points.length === 2) {
    const [x0, y0] = points[0]!
    const [x1, y1] = points[1]!
    return `M ${x0.toFixed(2)} ${y0.toFixed(2)} L ${x1.toFixed(2)} ${y1.toFixed(2)}`
  }
  const [startX, startY] = points[0]!
  const parts: string[] = [`M ${startX.toFixed(2)} ${startY.toFixed(2)}`]
  for (let i = 1; i < points.length - 1; i++) {
    const [cx, cy] = points[i]!
    const [nx, ny] = points[i + 1]!
    const mx = (cx + nx) / 2
    const my = (cy + ny) / 2
    parts.push(`Q ${cx.toFixed(2)} ${cy.toFixed(2)} ${mx.toFixed(2)} ${my.toFixed(2)}`)
  }
  const [lastX, lastY] = points[points.length - 1]!
  parts.push(`L ${lastX.toFixed(2)} ${lastY.toFixed(2)}`)
  return parts.join(' ')
}

function pointToSegmentDistSq(
  px: number,
  py: number,
  ax: number,
  ay: number,
  bx: number,
  by: number,
): number {
  const dx = bx - ax
  const dy = by - ay
  const lenSq = dx * dx + dy * dy
  if (lenSq <= 1e-6) {
    const ox = px - ax
    const oy = py - ay
    return ox * ox + oy * oy
  }
  const t = Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / lenSq))
  const projX = ax + t * dx
  const projY = ay + t * dy
  const ox = px - projX
  const oy = py - projY
  return ox * ox + oy * oy
}

/** Distance hit-test against a stroke polyline in world coordinates for eraser sweeping. */
export function isPointNearStroke(
  px: number,
  py: number,
  points: ReadonlyArray<readonly [number, number]>,
  hitRadius = 14,
): boolean {
  if (!points.length) return false
  const rSq = hitRadius * hitRadius
  if (points.length === 1) {
    const [ax, ay] = points[0]!
    const dx = px - ax
    const dy = py - ay
    return dx * dx + dy * dy <= rSq
  }
  for (let i = 0; i < points.length - 1; i++) {
    const [ax, ay] = points[i]!
    const [bx, by] = points[i + 1]!
    if (pointToSegmentDistSq(px, py, ax, ay, bx, by) <= rSq) return true
  }
  return false
}

export function extractMentions(text: string): string[] {
  const matches = text.match(/@([A-Za-z0-9_.\-]+)/g) ?? []
  const unique = new Set<string>()
  for (const raw of matches) {
    const normalized = raw.slice(1).trim()
    if (normalized) unique.add(normalized)
  }
  return [...unique]
}

export function hasAiOrTeamMention(pin: Pick<SpatialCommentPin, 'body' | 'mentions' | 'replies'>): boolean {
  const allMentions = new Set<string>([
    ...pin.mentions.map((m) => m.toLowerCase()),
    ...extractMentions(pin.body).map((m) => m.toLowerCase()),
    ...(pin.replies ?? []).flatMap((r) => extractMentions(r.body).map((m) => m.toLowerCase())),
  ])
  return allMentions.has('ai') || allMentions.has('team') || allMentions.size > 0
}
