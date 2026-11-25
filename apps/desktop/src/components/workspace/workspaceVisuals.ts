import type { UiWorkspaceFolderTone } from '@/composables/useUiPreferences'
import { resolveWebGatewayBase } from '@/lib/hostConnection'
import { encodeMediaPathToken } from '@/components/spatial/spatialWhiteboardTypes'

export type FileVisualCategory =
  | 'folder'
  | 'image'
  | 'pdf'
  | 'doc'
  | 'sheet'
  | 'code'
  | 'config'
  | 'archive'
  | 'media'
  | 'generic'

export interface FolderTonePalette {
  tone: UiWorkspaceFolderTone
  swatch: string
  swatchRing: string
  backTop: string
  backBottom: string
  backTab: string
  backStroke: string
  pocketWell: string
  flapTop: string
  flapMid: string
  flapBottom: string
  flapReadyTop: string
  flapReadyBottom: string
  specularTop: string
  refractionBottom: string
  ambientShadow: string
  readyGlow: string
  pillBg: string
  pillText: string
  pillBorder: string
}

export const FOLDER_TONE_PALETTES: Record<UiWorkspaceFolderTone, FolderTonePalette> = {
  amber: {
    tone: 'amber',
    swatch: '#f59e0b',
    swatchRing: 'rgba(245, 158, 11, 0.38)',
    backTop: '#fbbf24',
    backBottom: '#d97706',
    backTab: '#f59e0b',
    backStroke: 'rgba(254, 243, 199, 0.55)',
    pocketWell: 'rgba(120, 53, 15, 0.34)',
    flapTop: 'rgba(253, 224, 71, 0.52)',
    flapMid: 'rgba(251, 191, 36, 0.68)',
    flapBottom: 'rgba(217, 119, 6, 0.82)',
    flapReadyTop: 'rgba(254, 240, 138, 0.66)',
    flapReadyBottom: 'rgba(245, 158, 11, 0.86)',
    specularTop: 'rgba(255, 251, 235, 0.88)',
    refractionBottom: 'rgba(254, 215, 170, 0.42)',
    ambientShadow: 'rgba(217, 119, 6, 0.30)',
    readyGlow: 'rgba(251, 191, 36, 0.46)',
    pillBg: 'rgba(69, 26, 3, 0.36)',
    pillText: '#fffbeb',
    pillBorder: 'rgba(255, 251, 235, 0.34)',
  },
  teal: {
    tone: 'teal',
    swatch: '#14b8a6',
    swatchRing: 'rgba(20, 184, 166, 0.38)',
    backTop: '#2dd4bf',
    backBottom: '#0d9488',
    backTab: '#14b8a6',
    backStroke: 'rgba(204, 251, 241, 0.52)',
    pocketWell: 'rgba(19, 78, 74, 0.36)',
    flapTop: 'rgba(153, 246, 228, 0.48)',
    flapMid: 'rgba(45, 212, 191, 0.64)',
    flapBottom: 'rgba(13, 148, 136, 0.80)',
    flapReadyTop: 'rgba(167, 243, 208, 0.64)',
    flapReadyBottom: 'rgba(20, 184, 166, 0.86)',
    specularTop: 'rgba(240, 253, 250, 0.88)',
    refractionBottom: 'rgba(153, 246, 228, 0.42)',
    ambientShadow: 'rgba(13, 148, 136, 0.28)',
    readyGlow: 'rgba(45, 212, 191, 0.44)',
    pillBg: 'rgba(4, 47, 46, 0.38)',
    pillText: '#f0fdfa',
    pillBorder: 'rgba(240, 253, 250, 0.34)',
  },
  sky: {
    tone: 'sky',
    swatch: '#38bdf8',
    swatchRing: 'rgba(56, 189, 248, 0.38)',
    backTop: '#38bdf8',
    backBottom: '#0284c7',
    backTab: '#0ea5e9',
    backStroke: 'rgba(224, 242, 254, 0.55)',
    pocketWell: 'rgba(12, 74, 110, 0.36)',
    flapTop: 'rgba(186, 230, 253, 0.50)',
    flapMid: 'rgba(56, 189, 248, 0.65)',
    flapBottom: 'rgba(2, 132, 199, 0.82)',
    flapReadyTop: 'rgba(224, 242, 254, 0.66)',
    flapReadyBottom: 'rgba(14, 165, 233, 0.86)',
    specularTop: 'rgba(240, 249, 255, 0.90)',
    refractionBottom: 'rgba(186, 230, 253, 0.44)',
    ambientShadow: 'rgba(2, 132, 199, 0.28)',
    readyGlow: 'rgba(56, 189, 248, 0.44)',
    pillBg: 'rgba(8, 47, 73, 0.38)',
    pillText: '#f0f9ff',
    pillBorder: 'rgba(240, 249, 255, 0.34)',
  },
  coral: {
    tone: 'coral',
    swatch: '#fb7185',
    swatchRing: 'rgba(251, 113, 133, 0.38)',
    backTop: '#fb923c',
    backBottom: '#e11d48',
    backTab: '#f97316',
    backStroke: 'rgba(255, 228, 230, 0.54)',
    pocketWell: 'rgba(136, 19, 55, 0.36)',
    flapTop: 'rgba(254, 205, 211, 0.50)',
    flapMid: 'rgba(251, 113, 133, 0.66)',
    flapBottom: 'rgba(225, 29, 72, 0.80)',
    flapReadyTop: 'rgba(254, 215, 170, 0.64)',
    flapReadyBottom: 'rgba(244, 63, 94, 0.86)',
    specularTop: 'rgba(255, 241, 242, 0.90)',
    refractionBottom: 'rgba(254, 205, 211, 0.42)',
    ambientShadow: 'rgba(225, 29, 72, 0.28)',
    readyGlow: 'rgba(251, 113, 133, 0.44)',
    pillBg: 'rgba(76, 5, 25, 0.38)',
    pillText: '#fff1f2',
    pillBorder: 'rgba(255, 241, 242, 0.34)',
  },
  frost: {
    tone: 'frost',
    swatch: '#e2e8f0',
    swatchRing: 'rgba(148, 163, 184, 0.42)',
    backTop: '#e2e8f0',
    backBottom: '#94a3b8',
    backTab: '#cbd5e1',
    backStroke: 'rgba(255, 255, 255, 0.72)',
    pocketWell: 'rgba(30, 41, 59, 0.28)',
    flapTop: 'rgba(255, 255, 255, 0.60)',
    flapMid: 'rgba(226, 232, 240, 0.68)',
    flapBottom: 'rgba(148, 163, 184, 0.78)',
    flapReadyTop: 'rgba(241, 245, 249, 0.76)',
    flapReadyBottom: 'rgba(148, 163, 184, 0.86)',
    specularTop: 'rgba(255, 255, 255, 0.94)',
    refractionBottom: 'rgba(226, 232, 240, 0.56)',
    ambientShadow: 'rgba(100, 116, 139, 0.26)',
    readyGlow: 'rgba(148, 163, 184, 0.44)',
    pillBg: 'rgba(15, 23, 42, 0.42)',
    pillText: '#f8fafc',
    pillBorder: 'rgba(255, 255, 255, 0.45)',
  },
}

const IMAGE_EXTS = new Set([
  'png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'avif', 'bmp', 'ico', 'tif', 'tiff', 'heic',
])
const PDF_EXTS = new Set(['pdf'])
const DOC_EXTS = new Set(['md', 'mdx', 'markdown', 'doc', 'docx', 'rtf', 'odt', 'rst', 'tex'])
const SHEET_EXTS = new Set(['csv', 'tsv', 'xls', 'xlsx', 'ods', 'parquet', 'numbers'])
const CODE_EXTS = new Set([
  'ts', 'tsx', 'js', 'jsx', 'mjs', 'cjs', 'vue', 'svelte', 'py', 'rs', 'go', 'cs', 'fs',
  'java', 'kt', 'kts', 'swift', 'c', 'cc', 'cpp', 'h', 'hpp', 'html', 'htm', 'css', 'scss',
  'less', 'sh', 'bash', 'zsh', 'ps1', 'psm1', 'sql', 'rb', 'php', 'lua', 'dart', 'r', 'zig',
])
const CONFIG_EXTS = new Set([
  'json', 'jsonc', 'json5', 'toml', 'yaml', 'yml', 'ini', 'env', 'xml', 'plist', 'conf', 'cfg', 'lock', 'editorconfig',
])
const ARCHIVE_EXTS = new Set(['zip', 'tar', 'gz', 'tgz', '7z', 'rar', 'bz2', 'xz', 'zst', 'whl', 'jar'])
const MEDIA_EXTS = new Set([
  'mp3', 'wav', 'flac', 'ogg', 'm4a', 'aac', 'opus', 'mp4', 'webm', 'mov', 'mkv', 'avi', 'm4v',
])

export function extractFileExtension(pathOrName: string): string {
  const normalized = pathOrName.trim().replace(/\\/g, '/')
  const base = normalized.split('/').pop() ?? normalized
  if (base.startsWith('.') && !base.slice(1).includes('.')) {
    return base.slice(1).toLowerCase()
  }
  const dot = base.lastIndexOf('.')
  return dot > 0 ? base.slice(dot + 1).toLowerCase() : ''
}

export function classifyWorkspaceFileVisual(pathOrName: string): Exclude<FileVisualCategory, 'folder'> {
  const ext = extractFileExtension(pathOrName)
  if (!ext) return 'generic'
  if (IMAGE_EXTS.has(ext)) return 'image'
  if (PDF_EXTS.has(ext)) return 'pdf'
  if (DOC_EXTS.has(ext)) return 'doc'
  if (SHEET_EXTS.has(ext)) return 'sheet'
  if (CODE_EXTS.has(ext)) return 'code'
  if (CONFIG_EXTS.has(ext)) return 'config'
  if (ARCHIVE_EXTS.has(ext)) return 'archive'
  if (MEDIA_EXTS.has(ext)) return 'media'
  return 'generic'
}

export function fileBadgeLabel(pathOrName: string, category: Exclude<FileVisualCategory, 'folder'>): string {
  const ext = extractFileExtension(pathOrName).toUpperCase()
  if (category === 'pdf') return 'PDF'
  if (category === 'doc') return ext === 'MD' || ext === 'MDX' ? 'MD' : ext.slice(0, 4) || 'DOC'
  if (category === 'sheet') return ext.slice(0, 4) || 'XLS'
  if (category === 'code') return ext.slice(0, 4) || 'CODE'
  if (category === 'config') return ext.slice(0, 4) || 'CFG'
  if (category === 'archive') return ext.slice(0, 4) || 'ZIP'
  if (category === 'media') return ext.slice(0, 4) || 'AV'
  return ext.slice(0, 4) || 'TXT'
}

export function buildWorkspaceMediaThumbUrl(filePath: string): string {
  const trimmed = filePath.trim()
  if (!trimmed) return ''
  if (/^(?:https?:|data:|blob:|tinadec-media:)/i.test(trimmed) || trimmed.startsWith('/api/v1/fs/media')) {
    return trimmed
  }
  return `${resolveWebGatewayBase()}/api/v1/fs/media?token=${encodeMediaPathToken(trimmed)}`
}
