<script lang="ts">
import { defineSettingsModule } from '../defineSettingsModule'

export default defineSettingsModule('SettingsApiDocsModule')
</script>

<template>
<div class="settings-module" data-settings-module="apiDocs">
          <h2>{{ t('settings.apiDocs') }}</h2>
          <iframe class="api-docs-frame" :src="api.gatewayUrl + '/docs'" />
        </div>
</template>
