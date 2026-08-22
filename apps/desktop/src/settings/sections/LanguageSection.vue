<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { UiButton } from '@/components/ui'

/**
 * Language section extracted from SettingsPage (D7.2).
 *
 * Writes the same `tinadec-locale` localStorage key the app shell reads, so
 * switching here behaves exactly as the previous inline implementation did.
 */
const { t, locale } = useI18n()

function setLocale(lang: string): void {
  locale.value = lang
  localStorage.setItem('tinadec-locale', lang)
}
</script>

<template>
  <div>
    <h2>{{ t('settings.language') }}</h2>
    <div class="lang-options" data-testid="language-options">
      <UiButton
        variant="outline"
        :class="['lang-option', { active: locale === 'zh-CN' }]"
        data-testid="lang-zh"
        @click="setLocale('zh-CN')"
      >
        中文
      </UiButton>
      <UiButton
        variant="outline"
        :class="['lang-option', { active: locale === 'en' }]"
        data-testid="lang-en"
        @click="setLocale('en')"
      >
        English
      </UiButton>
    </div>
  </div>
</template>
