import * as THREE from 'three'
import type { FileVisualCategory } from './workspaceVisuals'

export type LivingFolderCardKind = 'folder' | 'photo' | 'doc' | 'code' | 'empty'

export interface CardTextureRequest {
  key: string
  kind: LivingFolderCardKind
  category: FileVisualCategory
  label: string
  badge: string
  thumbUrl: string
  isPrimary: boolean
  accentColor: string
  theme: 'dark' | 'light'
  emptyGuideLabel?: string
}

export interface LivingFolderTextureManager {
  buildSignature: (req: CardTextureRequest) => string
  getCardTexture: (req: CardTextureRequest) => THREE.CanvasTexture
  getCardRimColor: (req: CardTextureRequest) => THREE.Color
  disposeUnused: (activeSignatures: ReadonlySet<string>) => void
  disposeAll: () => void
}

const LOGICAL_WIDTH = 226
const LOGICAL_HEIGHT = 300

const UI_FONT_STACK =
  '"Geist", "Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif'
const MONO_FONT_STACK =
  '"Geist Mono", "JetBrains Mono", "Cascadia Code", "Fira Code", ui-monospace, SFMono-Regular, monospace'

interface CachedTextureEntry {
  signature: string
  canvas: HTMLCanvasElement
  texture: THREE.CanvasTexture
  disposed: boolean
}

function resolveDpr(): number {
  if (typeof window === 'undefined') return 1
  const raw = Number(window.devicePixelRatio) || 1
  return Math.min(Math.max(1, raw), 2.5)
}

function drawRoundedRectPath(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  w: number,
  h: number,
  r: number,
): void {
  const radius = Math.max(0, Math.min(r, w * 0.5, h * 0.5))
  ctx.beginPath()
  ctx.moveTo(x + radius, y)
  ctx.lineTo(x + w - radius, y)
  ctx.quadraticCurveTo(x + w, y, x + w, y + radius)
  ctx.lineTo(x + w, y + h - radius)
  ctx.quadraticCurveTo(x + w, y + h, x + w - radius, y + h)
  ctx.lineTo(x + radius, y + h)
  ctx.quadraticCurveTo(x, y + h, x, y + h - radius)
  ctx.lineTo(x, y + radius)
  ctx.quadraticCurveTo(x, y, x + radius, y)
  ctx.closePath()
}

function truncateMiddleToWidth(
  ctx: CanvasRenderingContext2D,
  text: string,
  maxWidth: number,
): string {
  const clean = text.trim() || 'untitled'
  if (ctx.measureText(clean).width <= maxWidth) {
    return clean
  }
  const ellipsis = '…'
  const dotIdx = clean.lastIndexOf('.')
  const tailTarget =
    dotIdx > 0 && clean.length - dotIdx <= 7
      ? clean.slice(dotIdx)
      : clean.slice(-Math.min(5, Math.floor(clean.length * 0.35)))

  let headLen = Math.max(1, clean.length - tailTarget.length - 1)
  while (headLen > 1) {
    const candidate = `${clean.slice(0, headLen)}${ellipsis}${tailTarget}`
    if (ctx.measureText(candidate).width <= maxWidth) {
      return candidate
    }
    headLen -= 1
  }

  let fallbackLen = clean.length - 1
  while (fallbackLen > 1) {
    const candidate = `${clean.slice(0, fallbackLen)}${ellipsis}`
    if (ctx.measureText(candidate).width <= maxWidth) {
      return candidate
    }
    fallbackLen -= 1
  }
  return clean.slice(0, 2)
}

function drawWrappedFilename(
  ctx: CanvasRenderingContext2D,
  text: string,
  x: number,
  y: number,
  maxWidth: number,
  lineHeight: number,
  maxLines = 2,
): void {
  const clean = text.trim() || 'untitled'
  if (maxLines <= 1 || ctx.measureText(clean).width <= maxWidth) {
    ctx.fillText(truncateMiddleToWidth(ctx, clean, maxWidth), x, y)
    return
  }

  // Split into first line + middle-truncated second line preserving extension
  let splitIdx = 1
  for (let i = 1; i <= clean.length; i++) {
    if (ctx.measureText(clean.slice(0, i)).width > maxWidth) {
      splitIdx = Math.max(1, i - 1)
      break
    }
    splitIdx = i
  }

  const firstLine = clean.slice(0, splitIdx)
  const remainder = clean.slice(splitIdx)
  ctx.fillText(firstLine, x, y)
  if (remainder.length > 0) {
    ctx.fillText(truncateMiddleToWidth(ctx, remainder, maxWidth), x, y + lineHeight)
  }
}

function drawStarIcon(
  ctx: CanvasRenderingContext2D,
  cx: number,
  cy: number,
  outerRadius: number,
  innerRadius: number,
): void {
  ctx.beginPath()
  for (let i = 0; i < 10; i++) {
    const radius = i % 2 === 0 ? outerRadius : innerRadius
    const angle = -Math.PI * 0.5 + (i * Math.PI) / 5
    const px = cx + Math.cos(angle) * radius
    const py = cy + Math.sin(angle) * radius
    if (i === 0) ctx.moveTo(px, py)
    else ctx.lineTo(px, py)
  }
  ctx.closePath()
}

function drawCoverImage(
  ctx: CanvasRenderingContext2D,
  img: HTMLImageElement,
  dx: number,
  dy: number,
  dw: number,
  dh: number,
): void {
  const iw = img.naturalWidth || img.width
  const ih = img.naturalHeight || img.height
  if (iw <= 0 || ih <= 0) return

  const scale = Math.max(dw / iw, dh / ih)
  const sw = dw / scale
  const sh = dh / scale
  const sx = (iw - sw) * 0.5
  const sy = (ih - sh) * 0.5
  ctx.drawImage(img, sx, sy, sw, sh, dx, dy, dw, dh)
}

function drawLandscapeFallback(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  w: number,
  h: number,
): void {
  const skyGrad = ctx.createLinearGradient(x, y, x, y + h)
  skyGrad.addColorStop(0, '#0ea5e9')
  skyGrad.addColorStop(0.55, '#6366f1')
  skyGrad.addColorStop(1, '#f472b6')
  ctx.fillStyle = skyGrad
  ctx.fillRect(x, y, w, h)

  // Warm sun + soft glow
  const sunX = x + w * 0.72
  const sunY = y + h * 0.28
  const halo = ctx.createRadialGradient(sunX, sunY, 4, sunX, sunY, 34)
  halo.addColorStop(0, 'rgba(254, 249, 195, 0.95)')
  halo.addColorStop(0.45, 'rgba(254, 240, 138, 0.42)')
  halo.addColorStop(1, 'rgba(254, 240, 138, 0)')
  ctx.fillStyle = halo
  ctx.beginPath()
  ctx.arc(sunX, sunY, 34, 0, Math.PI * 2)
  ctx.fill()

  ctx.fillStyle = '#fef08a'
  ctx.beginPath()
  ctx.arc(sunX, sunY, 14, 0, Math.PI * 2)
  ctx.fill()

  // Far mountain ridge
  ctx.fillStyle = 'rgba(30, 41, 59, 0.42)'
  ctx.beginPath()
  ctx.moveTo(x, y + h * 0.78)
  ctx.lineTo(x + w * 0.28, y + h * 0.44)
  ctx.lineTo(x + w * 0.54, y + h * 0.66)
  ctx.lineTo(x + w * 0.80, y + h * 0.40)
  ctx.lineTo(x + w, y + h * 0.62)
  ctx.lineTo(x + w, y + h)
  ctx.lineTo(x, y + h)
  ctx.closePath()
  ctx.fill()

  // Foreground alpine silhouette
  ctx.fillStyle = 'rgba(15, 23, 42, 0.68)'
  ctx.beginPath()
  ctx.moveTo(x, y + h * 0.86)
  ctx.lineTo(x + w * 0.34, y + h * 0.52)
  ctx.lineTo(x + w * 0.62, y + h * 0.76)
  ctx.lineTo(x + w * 0.78, y + h * 0.60)
  ctx.lineTo(x + w, y + h * 0.80)
  ctx.lineTo(x + w, y + h)
  ctx.lineTo(x, y + h)
  ctx.closePath()
  ctx.fill()
}

function drawPhotoTemplate(
  ctx: CanvasRenderingContext2D,
  req: CardTextureRequest,
  loadedImg: HTMLImageElement | null,
): void {
  const W = LOGICAL_WIDTH
  const H = LOGICAL_HEIGHT

  // Warm archival photo stock edge-to-edge (opaque for transmission buffer)
  const paperGrad = ctx.createLinearGradient(0, 0, 0, H)
  paperGrad.addColorStop(0, '#fffefb')
  paperGrad.addColorStop(1, '#f4f1ea')
  ctx.fillStyle = paperGrad
  ctx.fillRect(0, 0, W, H)

  // Subtle inset card border
  drawRoundedRectPath(ctx, 2, 2, W - 4, H - 4, 20)
  ctx.strokeStyle = 'rgba(15, 23, 42, 0.12)'
  ctx.lineWidth = 2
  ctx.stroke()

  const frameX = 14
  const frameY = 14
  const frameW = W - 28
  const frameH = 190

  ctx.save()
  drawRoundedRectPath(ctx, frameX, frameY, frameW, frameH, 12)
  ctx.clip()

  if (loadedImg && (loadedImg.naturalWidth > 0 || loadedImg.width > 0)) {
    drawCoverImage(ctx, loadedImg, frameX, frameY, frameW, frameH)
  } else {
    drawLandscapeFallback(ctx, frameX, frameY, frameW, frameH)
  }

  // Top translucent filename/type micro-bar so top peek & 65% hover pop both read crisply
  const topScrim = ctx.createLinearGradient(0, frameY, 0, frameY + 46)
  topScrim.addColorStop(0, 'rgba(15, 23, 42, 0.56)')
  topScrim.addColorStop(1, 'rgba(15, 23, 42, 0)')
  ctx.fillStyle = topScrim
  ctx.fillRect(frameX, frameY, frameW, 46)

  // Top-left badge pill inside photo
  const badgeText = (req.badge || 'IMG').slice(0, 5)
  ctx.font = `700 11px ${UI_FONT_STACK}`
  const badgeW = Math.max(34, Math.ceil(ctx.measureText(badgeText).width) + 14)
  drawRoundedRectPath(ctx, frameX + 8, frameY + 8, badgeW, 20, 6)
  ctx.fillStyle = 'rgba(15, 23, 42, 0.62)'
  ctx.fill()
  ctx.fillStyle = '#f8fafc'
  ctx.textBaseline = 'middle'
  ctx.textAlign = 'center'
  ctx.fillText(badgeText, frameX + 8 + badgeW * 0.5, frameY + 18.5)

  // Top-right filename pill inside photo header
  ctx.font = `600 11.5px ${UI_FONT_STACK}`
  ctx.textAlign = 'right'
  ctx.fillStyle = 'rgba(248, 250, 252, 0.96)'
  const maxTopLabelW = frameW - badgeW - 26
  if (maxTopLabelW > 36) {
    ctx.fillText(
      truncateMiddleToWidth(ctx, req.label, maxTopLabelW),
      frameX + frameW - 9,
      frameY + 18.5,
    )
  }

  ctx.restore()

  // Crisp inner highlight around photo viewport
  drawRoundedRectPath(ctx, frameX, frameY, frameW, frameH, 12)
  ctx.strokeStyle = 'rgba(15, 23, 42, 0.10)'
  ctx.lineWidth = 1.2
  ctx.stroke()

  // Crisp footer filename on Polaroid mat
  ctx.fillStyle = '#0f172a'
  ctx.font = `600 16px ${UI_FONT_STACK}`
  ctx.textAlign = 'left'
  ctx.textBaseline = 'top'
  drawWrappedFilename(ctx, req.label, 16, 218, W - 32, 21, 2)

  // Subtle warm accent line at bottom
  ctx.fillStyle = 'rgba(15, 23, 42, 0.08)'
  ctx.fillRect(16, H - 22, 48, 3)
}

function drawDocTemplate(ctx: CanvasRenderingContext2D, req: CardTextureRequest): void {
  const W = LOGICAL_WIDTH
  const H = LOGICAL_HEIGHT

  // Warm ivory paper background
  const paperGrad = ctx.createLinearGradient(0, 0, 0, H)
  paperGrad.addColorStop(0, '#fffdf7')
  paperGrad.addColorStop(1, '#f4efe3')
  ctx.fillStyle = paperGrad
  ctx.fillRect(0, 0, W, H)

  drawRoundedRectPath(ctx, 2, 2, W - 4, H - 4, 20)
  ctx.strokeStyle = 'rgba(120, 113, 108, 0.22)'
  ctx.lineWidth = 2
  ctx.stroke()

  // Top-right folded dog-ear + subtle cast shadow
  const fold = 40
  ctx.save()
  ctx.beginPath()
  ctx.moveTo(W - fold, 0)
  ctx.lineTo(W, fold)
  ctx.lineTo(W, 0)
  ctx.closePath()
  ctx.fillStyle = '#e7e0d0'
  ctx.fill()

  // Shadow under dog-ear flap
  ctx.shadowColor = 'rgba(28, 25, 23, 0.18)'
  ctx.shadowBlur = 8
  ctx.shadowOffsetX = -2
  ctx.shadowOffsetY = 3
  ctx.beginPath()
  ctx.moveTo(W - fold, 0)
  ctx.lineTo(W - fold, fold - 6)
  ctx.quadraticCurveTo(W - fold, fold, W - fold + 6, fold)
  ctx.lineTo(W, fold)
  ctx.closePath()
  const flapGrad = ctx.createLinearGradient(W - fold, 0, W, fold)
  flapGrad.addColorStop(0, '#faf6ec')
  flapGrad.addColorStop(1, '#e4dcc9')
  ctx.fillStyle = flapGrad
  ctx.fill()
  ctx.restore()

  // Top-left saturated category pill
  const badge = (req.badge || 'DOC').slice(0, 5)
  ctx.font = `700 13px ${UI_FONT_STACK}`
  const pillW = Math.max(46, Math.ceil(ctx.measureText(badge).width) + 20)
  const pillH = 28
  drawRoundedRectPath(ctx, 18, 18, pillW, pillH, 8)
  ctx.fillStyle = req.accentColor || '#dc2626'
  ctx.fill()

  ctx.fillStyle = '#ffffff'
  ctx.textAlign = 'center'
  ctx.textBaseline = 'middle'
  ctx.fillText(badge, 18 + pillW * 0.5, 18 + pillH * 0.5 + 0.5)

  // Prominent upper-half filename (fully visible when card pops 65-75% above flap!)
  ctx.fillStyle = '#1c1917'
  ctx.font = `650 17px ${UI_FONT_STACK}`
  ctx.textAlign = 'left'
  ctx.textBaseline = 'top'
  drawWrappedFilename(ctx, req.label, 18, 62, W - 36, 22, 2)

  // Neat editorial typographic lines
  const linesStartY = 122
  drawRoundedRectPath(ctx, 18, linesStartY, W - 56, 6, 3)
  ctx.fillStyle = req.accentColor || '#57534e'
  ctx.globalAlpha = 0.28
  ctx.fill()
  ctx.globalAlpha = 1

  const lineSpecs = [
    { y: linesStartY + 20, w: W - 38, alpha: 0.20 },
    { y: linesStartY + 36, w: W - 46, alpha: 0.16 },
    { y: linesStartY + 52, w: W - 68, alpha: 0.16 },
    { y: linesStartY + 74, w: W - 42, alpha: 0.13 },
    { y: linesStartY + 90, w: W - 92, alpha: 0.13 },
  ]
  for (const spec of lineSpecs) {
    drawRoundedRectPath(ctx, 18, spec.y, spec.w, 5.5, 2.75)
    ctx.fillStyle = `rgba(68, 64, 60, ${spec.alpha})`
    ctx.fill()
  }

  // Crisp bottom caption strip
  ctx.fillStyle = '#44403c'
  ctx.font = `600 13.5px ${UI_FONT_STACK}`
  ctx.textBaseline = 'bottom'
  ctx.fillText(truncateMiddleToWidth(ctx, req.label, W - 36), 18, H - 18)
}

function drawCodeTemplate(ctx: CanvasRenderingContext2D, req: CardTextureRequest): void {
  const W = LOGICAL_WIDTH
  const H = LOGICAL_HEIGHT

  // Deep indigo/slate IDE card
  const bgGrad = ctx.createLinearGradient(0, 0, 0, H)
  bgGrad.addColorStop(0, '#131c33')
  bgGrad.addColorStop(0.6, '#171f3a')
  bgGrad.addColorStop(1, '#0f172a')
  ctx.fillStyle = bgGrad
  ctx.fillRect(0, 0, W, H)

  drawRoundedRectPath(ctx, 2, 2, W - 4, H - 4, 20)
  ctx.strokeStyle = 'rgba(129, 140, 248, 0.32)'
  ctx.lineWidth = 2
  ctx.stroke()

  // Top header: </> glyph + language badge
  ctx.font = `700 16px ${MONO_FONT_STACK}`
  ctx.fillStyle = '#38bdf8'
  ctx.textAlign = 'left'
  ctx.textBaseline = 'middle'
  ctx.fillText('</>', 18, 32)

  const badge = (req.badge || 'CODE').slice(0, 5)
  ctx.font = `700 12px ${MONO_FONT_STACK}`
  const pillW = Math.max(42, Math.ceil(ctx.measureText(badge).width) + 18)
  const pillX = W - 18 - pillW
  drawRoundedRectPath(ctx, pillX, 19, pillW, 25, 7)
  ctx.fillStyle = 'rgba(99, 102, 241, 0.26)'
  ctx.fill()
  ctx.strokeStyle = 'rgba(165, 180, 252, 0.42)'
  ctx.lineWidth = 1.2
  ctx.stroke()

  ctx.fillStyle = '#e0e7ff'
  ctx.textAlign = 'center'
  ctx.fillText(badge, pillX + pillW * 0.5, 32)

  // Prominent high-contrast filename in upper zone
  ctx.fillStyle = '#f8fafc'
  ctx.font = `600 16px ${MONO_FONT_STACK}`
  ctx.textAlign = 'left'
  ctx.textBaseline = 'top'
  drawWrappedFilename(ctx, req.label, 18, 60, W - 36, 21, 2)

  // 3+ colored syntax bars with realistic token segments
  const syntaxRows: Array<Array<{ x: number; w: number; color: string }>> = [
    [
      { x: 18, w: 38, color: '#f472b6' },
      { x: 62, w: 68, color: '#38bdf8' },
      { x: 136, w: 46, color: '#cbd5e1' },
    ],
    [
      { x: 30, w: 52, color: '#a78bfa' },
      { x: 88, w: 86, color: '#34d399' },
    ],
    [
      { x: 30, w: 42, color: '#fbbf24' },
      { x: 78, w: 64, color: '#60a5fa' },
      { x: 148, w: 34, color: '#94a3b8' },
    ],
  ]

  syntaxRows.forEach((tokens, rowIdx) => {
    const ry = 122 + rowIdx * 24
    for (const seg of tokens) {
      drawRoundedRectPath(ctx, seg.x, ry, seg.w, 8, 4)
      ctx.fillStyle = seg.color
      ctx.fill()
    }
  })

  // Subtle divider + crisp light footer filename
  ctx.fillStyle = 'rgba(148, 163, 184, 0.16)'
  ctx.fillRect(18, H - 48, W - 36, 1)

  ctx.fillStyle = '#e2e8f0'
  ctx.font = `500 13px ${MONO_FONT_STACK}`
  ctx.textBaseline = 'bottom'
  ctx.fillText(truncateMiddleToWidth(ctx, req.label, W - 36), 18, H - 18)
}

function drawFolderTemplate(ctx: CanvasRenderingContext2D, req: CardTextureRequest): void {
  const W = LOGICAL_WIDTH
  const H = LOGICAL_HEIGHT

  // Upper sky/header backdrop above the mini folder ear so rounded top stays opaque & clean
  ctx.fillStyle = req.theme === 'dark' ? '#1e293b' : '#f8fafc'
  ctx.fillRect(0, 0, W, H)

  // Back ear tab silhouette at top-left
  ctx.beginPath()
  ctx.moveTo(8, 52)
  ctx.lineTo(8, 22)
  ctx.quadraticCurveTo(8, 10, 20, 10)
  ctx.lineTo(94, 10)
  ctx.quadraticCurveTo(103, 10, 109, 18)
  ctx.lineTo(120, 32)
  ctx.lineTo(W - 8, 32)
  ctx.lineTo(W - 8, 60)
  ctx.closePath()
  ctx.fillStyle = req.accentColor || '#f59e0b'
  ctx.fill()

  // Ear highlight crease
  ctx.strokeStyle = 'rgba(255, 255, 255, 0.52)'
  ctx.lineWidth = 2
  ctx.lineCap = 'round'
  ctx.beginPath()
  ctx.moveTo(22, 19)
  ctx.lineTo(88, 19)
  ctx.stroke()

  // Warm folder front cardstock body
  const bodyGrad = ctx.createLinearGradient(0, 30, 0, H)
  bodyGrad.addColorStop(0, '#fef3c7')
  bodyGrad.addColorStop(0.45, '#fde68a')
  bodyGrad.addColorStop(1, '#fcd34d')
  drawRoundedRectPath(ctx, 6, 30, W - 12, H - 36, 16)
  ctx.fillStyle = bodyGrad
  ctx.fill()

  ctx.strokeStyle = 'rgba(180, 83, 9, 0.24)'
  ctx.lineWidth = 1.6
  ctx.stroke()

  // Colored top accent band on folder body
  drawRoundedRectPath(ctx, 18, 46, 52, 22, 7)
  ctx.fillStyle = 'rgba(120, 53, 15, 0.14)'
  ctx.fill()
  ctx.fillStyle = '#78350f'
  ctx.font = `700 11px ${UI_FONT_STACK}`
  ctx.textAlign = 'center'
  ctx.textBaseline = 'middle'
  ctx.fillText('DIR', 44, 57.5)

  // Golden Star badge when primary root
  if (req.isPrimary) {
    const starCx = W - 34
    const starCy = 57
    ctx.beginPath()
    ctx.arc(starCx, starCy, 14, 0, Math.PI * 2)
    ctx.fillStyle = '#78350f'
    ctx.fill()

    drawStarIcon(ctx, starCx, starCy, 8.5, 4.1)
    ctx.fillStyle = '#fbbf24'
    ctx.fill()
  }

  // Crisp high-contrast folder name in upper-middle zone
  ctx.fillStyle = '#1c1917'
  ctx.font = `700 18px ${UI_FONT_STACK}`
  ctx.textAlign = 'left'
  ctx.textBaseline = 'top'
  drawWrappedFilename(ctx, req.label, 18, 84, W - 36, 23, 2)

  // Subtle embossed folder grooves
  drawRoundedRectPath(ctx, 18, 154, W - 58, 5, 2.5)
  ctx.fillStyle = 'rgba(120, 53, 15, 0.16)'
  ctx.fill()
  drawRoundedRectPath(ctx, 18, 168, W - 92, 5, 2.5)
  ctx.fillStyle = 'rgba(120, 53, 15, 0.11)'
  ctx.fill()

  // Footer folder label
  ctx.fillStyle = '#451a03'
  ctx.font = `600 13.5px ${UI_FONT_STACK}`
  ctx.textBaseline = 'bottom'
  ctx.fillText(truncateMiddleToWidth(ctx, req.label, W - 36), 18, H - 18)
}

function drawEmptyTemplate(ctx: CanvasRenderingContext2D, req: CardTextureRequest): void {
  const W = LOGICAL_WIDTH
  const H = LOGICAL_HEIGHT
  const isDark = req.theme === 'dark'

  ctx.fillStyle = isDark ? '#1e293b' : '#f8fafc'
  ctx.fillRect(0, 0, W, H)

  // Soft dashed rounded guide frame
  ctx.save()
  drawRoundedRectPath(ctx, 12, 12, W - 24, H - 24, 16)
  ctx.setLineDash([8, 6])
  ctx.strokeStyle = isDark ? 'rgba(148, 163, 184, 0.48)' : 'rgba(100, 116, 139, 0.44)'
  ctx.lineWidth = 2
  ctx.stroke()
  ctx.restore()

  // Upper-center plus glyph badge
  const cx = W * 0.5
  const cy = 78
  drawRoundedRectPath(ctx, cx - 20, cy - 20, 40, 40, 12)
  ctx.fillStyle = isDark ? 'rgba(56, 189, 248, 0.16)' : 'rgba(14, 165, 233, 0.12)'
  ctx.fill()

  ctx.strokeStyle = isDark ? '#7dd3fc' : '#0284c7'
  ctx.lineWidth = 2.4
  ctx.lineCap = 'round'
  ctx.beginPath()
  ctx.moveTo(cx - 9, cy)
  ctx.lineTo(cx + 9, cy)
  ctx.moveTo(cx, cy - 9)
  ctx.lineTo(cx, cy + 9)
  ctx.stroke()

  const guide = (req.emptyGuideLabel || '拖入文件或文件夹').trim()
  ctx.fillStyle = isDark ? '#e2e8f0' : '#334155'
  ctx.font = `600 15px ${UI_FONT_STACK}`
  ctx.textAlign = 'center'
  ctx.textBaseline = 'top'
  drawWrappedFilename(ctx, guide, cx, 132, W - 44, 21, 2)
}

function renderCardToCanvas(
  canvas: HTMLCanvasElement,
  dpr: number,
  req: CardTextureRequest,
  loadedImg: HTMLImageElement | null,
): void {
  const physW = Math.round(LOGICAL_WIDTH * dpr)
  const physH = Math.round(LOGICAL_HEIGHT * dpr)
  if (canvas.width !== physW || canvas.height !== physH) {
    canvas.width = physW
    canvas.height = physH
  }

  const ctx = canvas.getContext('2d')
  if (!ctx) return

  ctx.save()
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
  ctx.clearRect(0, 0, LOGICAL_WIDTH, LOGICAL_HEIGHT)

  switch (req.kind) {
    case 'photo':
      drawPhotoTemplate(ctx, req, loadedImg)
      break
    case 'code':
      drawCodeTemplate(ctx, req)
      break
    case 'folder':
      drawFolderTemplate(ctx, req)
      break
    case 'empty':
      drawEmptyTemplate(ctx, req)
      break
    case 'doc':
    default:
      drawDocTemplate(ctx, req)
      break
  }

  ctx.restore()
}

export function createCardRimColor(
  kind: LivingFolderCardKind,
  theme: 'dark' | 'light',
  accentColor?: string,
): THREE.Color {
  if (kind === 'code') {
    return new THREE.Color('#273252')
  }
  if (kind === 'folder') {
    const base = new THREE.Color('#f59e0b')
    if (accentColor && accentColor.startsWith('#')) {
      base.set(accentColor).lerp(new THREE.Color('#fde68a'), 0.45)
    }
    return base
  }
  if (kind === 'empty') {
    return new THREE.Color(theme === 'dark' ? '#334155' : '#cbd5e1')
  }
  if (kind === 'photo') {
    return new THREE.Color('#ece8df')
  }
  return new THREE.Color('#ebe4d4')
}

export function createLivingFolderTextureManager(options: {
  maxAnisotropy: number
  onTextureUpdated: () => void
}): LivingFolderTextureManager {
  const anisotropy = Math.min(Math.max(1, options.maxAnisotropy || 1), 8)
  const cache = new Map<string, CachedTextureEntry>()
  const imageCache = new Map<string, HTMLImageElement | 'loading' | 'error'>()
  const waitingCallbacks = new Map<string, Set<() => void>>()
  let managerDisposed = false

  function loadImage(url: string, onReady: (img: HTMLImageElement) => void): HTMLImageElement | null {
    if (!url || typeof Image === 'undefined') return null
    const existing = imageCache.get(url)
    if (existing instanceof HTMLImageElement) {
      return existing
    }
    if (existing === 'error') {
      return null
    }
    let waiters = waitingCallbacks.get(url)
    if (!waiters) {
      waiters = new Set()
      waitingCallbacks.set(url, waiters)
    }
    waiters.add(() => {
      const resolved = imageCache.get(url)
      if (resolved instanceof HTMLImageElement) {
        onReady(resolved)
      }
    })

    if (existing === 'loading') {
      return null
    }

    imageCache.set(url, 'loading')
    const img = new Image()
    img.crossOrigin = 'anonymous'
    img.decoding = 'async'
    img.onload = () => {
      if (managerDisposed) return
      imageCache.set(url, img)
      const pending = waitingCallbacks.get(url)
      waitingCallbacks.delete(url)
      pending?.forEach((fn) => fn())
    }
    img.onerror = () => {
      if (managerDisposed) return
      imageCache.set(url, 'error')
      waitingCallbacks.delete(url)
    }
    img.src = url
    return null
  }

  function buildSignature(req: CardTextureRequest): string {
    return [
      req.key,
      req.kind,
      req.category,
      req.badge,
      req.label,
      req.isPrimary ? '1' : '0',
      req.thumbUrl,
      req.theme,
      req.accentColor,
      req.emptyGuideLabel ?? '',
    ].join('|')
  }

  function getCardTexture(req: CardTextureRequest): THREE.CanvasTexture {
    const signature = buildSignature(req)
    const hit = cache.get(signature)
    if (hit && !hit.disposed) {
      return hit.texture
    }

    const dpr = resolveDpr()
    const canvas = document.createElement('canvas')
    const texture = new THREE.CanvasTexture(canvas)
    texture.colorSpace = THREE.SRGBColorSpace
    texture.anisotropy = anisotropy
    texture.minFilter = THREE.LinearMipmapLinearFilter
    texture.magFilter = THREE.LinearFilter
    texture.wrapS = THREE.ClampToEdgeWrapping
    texture.wrapT = THREE.ClampToEdgeWrapping

    const entry: CachedTextureEntry = {
      signature,
      canvas,
      texture,
      disposed: false,
    }
    cache.set(signature, entry)

    const initialImg =
      req.kind === 'photo' && req.thumbUrl
        ? loadImage(req.thumbUrl, (loaded) => {
            if (managerDisposed || entry.disposed) return
            renderCardToCanvas(entry.canvas, resolveDpr(), req, loaded)
            entry.texture.needsUpdate = true
            options.onTextureUpdated()
          })
        : null

    renderCardToCanvas(canvas, dpr, req, initialImg)
    texture.needsUpdate = true
    return texture
  }

  function getCardRimColor(req: CardTextureRequest): THREE.Color {
    return createCardRimColor(req.kind, req.theme, req.accentColor)
  }

  function disposeEntry(entry: CachedTextureEntry): void {
    if (entry.disposed) return
    entry.disposed = true
    entry.texture.dispose()
    entry.canvas.width = 0
    entry.canvas.height = 0
  }

  function disposeUnused(activeSignatures: ReadonlySet<string>): void {
    for (const [sig, entry] of cache.entries()) {
      if (!activeSignatures.has(sig)) {
        disposeEntry(entry)
        cache.delete(sig)
      }
    }
  }

  function disposeAll(): void {
    managerDisposed = true
    for (const entry of cache.values()) {
      disposeEntry(entry)
    }
    cache.clear()
    waitingCallbacks.clear()
    imageCache.clear()
  }

  return {
    buildSignature,
    getCardTexture,
    getCardRimColor,
    disposeUnused,
    disposeAll,
  }
}
