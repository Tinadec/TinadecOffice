<script setup lang="ts">
import { UiButton } from '@/components/ui'
import { useI18n } from 'vue-i18n'

const { t, locale } = useI18n()

function setLocale(lang: string) {
  locale.value = lang
  localStorage.setItem('tinadec-locale', lang)
}
</script>

<template>
  <h1>{{ t('settings.language') }}</h1>
            <div class="lang-options">
              <UiButton
                variant="outline"
                :class="['lang-option', { active: locale === 'zh-CN' }]"
                @click="setLocale('zh-CN')"
              >
                中文
              </UiButton>
              <UiButton
                variant="outline"
                :class="['lang-option', { active: locale === 'en' }]"
                @click="setLocale('en')"
              >
                English
              </UiButton>
            </div>
</template>
