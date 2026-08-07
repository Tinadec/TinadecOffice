import { createApp } from 'vue'
import { vaporInteropPlugin } from 'vue'
import App from './App.vue'
import router from './router'
import i18n from './i18n'
import { useTheme } from './composables/useTheme'
import { setNotificationFallbackText } from './composables/useNotifications'
import { installPreviewShimIfNeeded } from './lib/previewShim'
import { installRendererErrorFallback } from './lib/rendererErrorFallback'
import './styles.css'
// Overrides so legacy page components (AppSidebar/ChatPanel) fill their card frame.
import './workbench/components/workbench-card-fill.css'

// Install a minimal `window.tinadec` when running without the Electron preload
// (bare-vite preview / MCP pane) so pages render instead of crashing.
installPreviewShimIfNeeded()

const app = createApp(App)

// Vapor SFCs (via `<template vapor>`) render through the Vapor renderer; the
// interop plugin lets them live inside the classic vdom tree (e.g. Ui primitives
// used by splash/notifications) while the rest of the app stays classic.
app.use(vaporInteropPlugin)

app.use(router)
app.use(i18n)

// Inject localized fallback text for notification composables
// so unknownError follows the active locale.
setNotificationFallbackText(i18n.global.t('app.unknownError'))

// 在挂载前初始化主题，确保 DOM 准备好后再应用样式
const { applyInitialTheme } = useTheme()
if (applyInitialTheme) {
  applyInitialTheme()
}

// Global error containment: logs every uncaught error and, on the first fatal
// one, swaps the stuck splash for a recoverable DOM fallback instead of leaving
// a silent solid-color window. Installed before mount so render errors during
// boot are also caught.
const report = installRendererErrorFallback(app)
try {
  app.mount('#app')
} catch (err) {
  // A throw at mount means nothing rendered — this is fatal, show the fallback.
  report(err, { source: 'app.mount' }, true)
}

