<script setup lang="ts">
// PreviewNavScope.vue — navigation isolation for real pages embedded in the
// Debug Studio preview viewport.
//
// Real route pages (SettingsPage / CodePage / MarketPage) contain back
// buttons and cross-links (`router.push('/')`, `router.push('/market')`).
// Inside the preview they must NOT navigate this window: landing on '/'
// would mount HomePage, which re-initializes the UIE engine WITH the
// Electron persistence adapter and could autosave over the user's real
// workbench layout. We re-provide vue-router's `routerKey` with a facade
// whose location-changing methods are no-ops; read-only APIs pass through.
import { provide } from 'vue'
import { useRouter, routerKey, type Router } from 'vue-router'
import { useNotifications } from '@/composables/useNotifications'

const real = useRouter()
const { notify } = useNotifications()

function blocked(): Promise<void> {
  notify.info({ message: '预览模式：页面导航已隔离', source: 'debug-preview' })
  return Promise.resolve()
}

const facade = {
  ...real,
  push: blocked,
  replace: blocked,
  back: () => void blocked(),
  forward: () => void blocked(),
  go: () => void blocked(),
} as unknown as Router

provide(routerKey, facade)
</script>

<template>
  <slot />
</template>
