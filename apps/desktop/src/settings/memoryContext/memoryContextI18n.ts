import { useI18n } from 'vue-i18n'
import type { SupportedLocale } from '@/i18n'
import en from '@/locales/en'
import ja from '@/locales/ja'
import ru from '@/locales/ru'
import zhCN from '@/locales/zh-CN'
import zhTW from '@/locales/zh-TW'

type LocaleBundle = {
  settings: { memoryContext: string }
  commandPanel: { contextStrategy: string }
  memoryContext: Record<string, unknown>
}

function flattenMemoryContext(bundle: LocaleBundle): Record<string, string> {
  const out: Record<string, string> = {
    'settings.memoryContext': bundle.settings.memoryContext,
    'commandPanel.contextStrategy': bundle.commandPanel.contextStrategy,
  }
  const walk = (node: Record<string, unknown>, prefix: string) => {
    for (const [key, value] of Object.entries(node)) {
      const next = `${prefix}.${key}`
      if (value && typeof value === 'object') {
        walk(value as Record<string, unknown>, next)
      } else {
        out[next] = String(value)
      }
    }
  }
  walk(bundle.memoryContext, 'memoryContext')
  return out
}

/**
 * Compatibility projection over the canonical `locales/{zh-CN,zh-TW,en,ja,ru}.ts` bundles.
 * Components should use `useI18n()` directly; this thin re-export preserves legacy test imports.
 */
export const MEMORY_CONTEXT_MESSAGES: Record<SupportedLocale, Record<string, string>> = {
  'zh-CN': flattenMemoryContext(zhCN as unknown as LocaleBundle),
  'zh-TW': flattenMemoryContext(zhTW as unknown as LocaleBundle),
  en: flattenMemoryContext(en as unknown as LocaleBundle),
  ja: flattenMemoryContext(ja as unknown as LocaleBundle),
  ru: flattenMemoryContext(ru as unknown as LocaleBundle),
}

export function useMemoryContextI18n() {
  const { t, te, locale } = useI18n()
  return { t, tx: t, te, locale }
}
