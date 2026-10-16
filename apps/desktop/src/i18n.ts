/**
 * The one i18n instance. Interface text lives only in locales/{en,zh-CN}.ts: components use useI18n(),
 * module code i18n.global.t, pure helpers take a `Translate`; dates and numbers format with the active locale.
 */
import { createI18n } from 'vue-i18n'
import zhCN from './locales/zh-CN'
import en from './locales/en'

// 延迟获取 localStorage，避免在模块加载时访问
function getSavedLocale(): string {
  if (typeof window !== 'undefined' && window.localStorage) {
    return localStorage.getItem('tinadec-locale') ?? 'zh-CN'
  }
  return 'zh-CN'
}

const i18n = createI18n({
  legacy: false,
  locale: getSavedLocale(),
  fallbackLocale: 'en',
  messages: {
    'zh-CN': zhCN,
    en,
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
