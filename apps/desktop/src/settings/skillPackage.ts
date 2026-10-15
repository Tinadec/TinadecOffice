import i18n from '@/i18n'

/** Local import validation mirrors the package boundary enforced again by Core. */
export const SKILL_PACKAGE_MAX_BYTES = 16 * 1024 * 1024
export const SKILL_PACKAGE_MAX_FILES = 256
export const SKILL_ASSET_MAX_BYTES = 4 * 1024 * 1024

export function skillNameFromDocument(content: string): string {
  const frontmatter = /^\uFEFF?---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/.exec(content)?.[1]
  if (!frontmatter) return ''
  const raw = /^name\s*:\s*(.+?)\s*$/m.exec(frontmatter)?.[1] ?? ''
  return raw.replace(/^(['"])(.*)\1$/, '$2').trim()
}

export function validateSkillPackagePaths(paths: string[]): void {
  if (paths.length > SKILL_PACKAGE_MAX_FILES) throw new Error(i18n.global.t('toolsSettings.skillPackageTooMany', { max: SKILL_PACKAGE_MAX_FILES }))
  const seen = new Set<string>()
  for (const path of paths) {
    if (!path || path.startsWith('/') || path.includes('\\') || path.includes(':') || path.split('/').some(segment => !segment || segment === '.' || segment === '..')) throw new Error(i18n.global.t('toolsSettings.skillPackagePathInvalid', { path: path }))
    const canonical = path.toLowerCase()
    if (seen.has(canonical)) throw new Error(i18n.global.t('toolsSettings.skillPackagePathDuplicate', { path }))
    seen.add(canonical)
  }
}

export function decodeSkillAsset(base64: string): string | null {
  try {
    const bytes = Uint8Array.from(atob(base64), character => character.charCodeAt(0))
    if (bytes.includes(0)) return null
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  } catch { return null }
}
