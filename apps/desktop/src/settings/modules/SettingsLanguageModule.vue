<script lang="ts">
import { defineSettingsModule } from '../defineSettingsModule'

export default defineSettingsModule('SettingsLanguageModule')
</script>

<template>
<div class="settings-module" data-settings-module="language">
          <h2>{{ t('settings.language') }}</h2>
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
        </div>
</template>
