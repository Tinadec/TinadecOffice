/**
 * The one i18n instance. Interface text lives only in locales/{zh-CN,zh-TW,en,ja,ru}.ts, which all carry
 * the same keys (locales/i18nParity.test.ts): components use useI18n(), module code i18n.global.t, pure
 * helpers take a `Translate`; dates and numbers format with the active locale.
 */
import { createI18n } from 'vue-i18n'
import zhCN from './locales/zh-CN'
import zhTW from './locales/zh-TW'
import en from './locales/en'
import ja from './locales/ja'
import ru from './locales/ru'

/** The interface languages, in the order the language picker lists them. */
export const SUPPORTED_LOCALES = ['zh-CN', 'zh-TW', 'en', 'ja', 'ru'] as const
export type SupportedLocale = (typeof SUPPORTED_LOCALES)[number]

export function isSupportedLocale(value: unknown): value is SupportedLocale {
  return typeof value === 'string' && (SUPPORTED_LOCALES as readonly string[]).includes(value)
}

// 延迟获取 localStorage，避免在模块加载时访问；存了不认识的值就回到默认语言
function getSavedLocale(): SupportedLocale {
  if (typeof window !== 'undefined' && window.localStorage) {
    const saved = localStorage.getItem('tinadec-locale')
    if (isSupportedLocale(saved)) return saved
  }
  return 'zh-CN'
}

/**
 * Russian has three plural forms, written `one | few | many`: 1, 21, 31… / 2–4, 22–24… / everything
 * else (0, 5–20, 25…). A message with fewer forms uses its last one.
 */
export function russianPluralRule(choice: number, choicesLength: number): number {
  const n = Math.abs(choice)
  const lastDigit = n % 10
  const lastTwo = n % 100
  const form = lastDigit === 1 && lastTwo !== 11 ? 0
    : lastDigit >= 2 && lastDigit <= 4 && (lastTwo < 10 || lastTwo >= 20) ? 1
    : 2
  return Math.min(form, choicesLength - 1)
}

const i18n = createI18n({
  legacy: false,
  locale: getSavedLocale(),
  // A Traditional Chinese reader is better served by Simplified than by English.
  fallbackLocale: { 'zh-TW': ['zh-CN', 'en'], default: ['en'] },
  pluralRules: { ru: russianPluralRule },
  messages: {
    'zh-CN': zhCN,
    'zh-TW': zhTW,
    en,
    ja,
    ru,
  },
})

/**
 * The forms of useI18n()'s `t` that plain helpers take from their caller, so their wording
 * follows the component's language without the helper importing this instance.
 */
export interface Translate {
  (key: string): string
  (key: string, named: Record<string, unknown>): string
  (key: string, named: Record<string, unknown>, plural: number): string
}

export default i18n
