import { createApp, watch } from 'vue'
import { createPinia } from 'pinia'
import * as Vue from 'vue'
// ponytail: vue 3.5 has no vaporInteropPlugin; 3.6-rc does — tolerate either
const vaporInteropPlugin = (Vue as unknown as Record<string, unknown>).vaporInteropPlugin as Parameters<ReturnType<typeof createApp>['use']>[0] | undefined
import App from './App.vue'
import router from './router'
import i18n, { ensureActiveLocale, isSupportedLocale, setInterfaceLocale } from './i18n'
import { useTheme } from './composables/useTheme'
import { useDynamicPalette } from './composables/useDynamicPalette'
import { setErrorActionLabels, setLocalizedConnectionMessages, setNotificationFallbackText } from './composables/useNotifications'
import { installPreviewShimIfNeeded } from './lib/previewShim'
import { homeController } from './controllers/HomeController'
import { auth } from './lib/auth'
import { installRendererErrorFallback } from './lib/rendererErrorFallback'
import './styles.css'
import { installCodeFont } from '@/lib/codeFont'
installCodeFont()

// Install a minimal `window.tinadec` when running without the Electron preload
// (bare-vite preview / MCP pane) so pages render instead of crashing.
installPreviewShimIfNeeded()

const app = createApp(App)
const pinia = createPinia()

// Vapor SFCs (via `<template vapor>`) render through the Vapor renderer; the
// interop plugin lets them live inside the classic vdom tree (e.g. Ui primitives
// used by splash/notifications) while the rest of the app stays classic.
app.use(pinia)
if (vaporInteropPlugin) app.use(vaporInteropPlugin)

app.use(router)
app.use(i18n)

// Register recovery handlers before mount so the first classified error already offers its
// escape hatch (retry / unregister). The controller is the singleton that owns the roster.
homeController.installErrorRecovery()

// Notification text kept outside Vue's reactivity (the unknown-error fallback and the recovery
// labels for server-classified errors; the server names an action kind, the wording lives here so
// the API never ships UI copy) and the document language follow the interface language: applied
// now and again on every switch.
function applyLocaleText(): void {
  document.documentElement.lang = i18n.global.locale.value
  // The tray menu is drawn by the main process, which has no access to these bundles: tell it which
  // language this window is actually showing so its labels follow a switch made in Settings.
  window.tinadec?.setInterfaceLocale?.(i18n.global.locale.value)
  setNotificationFallbackText(i18n.global.t('app.unknownError'))
  setLocalizedConnectionMessages([
    i18n.global.t('host.identityUnavailable'),
    i18n.global.t('host.identityRejected'),
    i18n.global.t('host.verifyingReason'),
    i18n.global.t('host.notReady'),
    i18n.global.t('host.bridgeUnavailable'),
    i18n.global.t('host.connectionCheckFailed'),
    i18n.global.t('host.connectionStopped'),
    i18n.global.t('host.authRequiredReason'),
    i18n.global.t('errors.networkUnavailable'),
  ])
  setErrorActionLabels({
    retry: i18n.global.t('errors.actions.retry'),
    reload: i18n.global.t('errors.actions.reload'),
    open_settings: i18n.global.t('errors.actions.openSettings'),
    open_storage_settings: i18n.global.t('errors.actions.openStorageSettings'),
    open_tool_settings: i18n.global.t('errors.actions.openToolSettings'),
    unregister_workspace: i18n.global.t('errors.actions.unregisterWorkspace'),
    choose_folder: i18n.global.t('errors.actions.chooseFolder'),
  })
}
// A window that starts in a saved non-default language must have that bundle before the first paint:
// the strings registered below (unknown-error fallback, server-classified action labels) live outside
// Vue and are only recomputed when the locale changes, and the mounted tree would render in a fallback
// language until the chunk landed. zh-CN is in the entry chunk, so a default boot awaits nothing.
if (i18n.global.locale.value !== 'zh-CN') {
  try {
    await ensureActiveLocale()
  } catch {
    // The chunk could not be fetched; a complete language beats dotted keys.
    i18n.global.locale.value = 'zh-CN'
  }
}
applyLocaleText()
watch(() => i18n.global.locale.value, applyLocaleText)
// A language picked in one window reaches the other open windows (detached panels, pets) through
// the localStorage key i18n.ts reads on boot.
window.addEventListener('storage', (event) => {
  // The announced language's bundle lives in another chunk; switch only once it is here, otherwise the
  // window repaints into a language it does not have yet. A failed load keeps the current language.
  if (event.key !== 'tinadec-locale' || !isSupportedLocale(event.newValue)) return
  if (event.newValue === i18n.global.locale.value) return
  void setInterfaceLocale(event.newValue)
})

// 在挂载前初始化主题，确保 DOM 准备好后再应用样式
const { applyInitialTheme } = useTheme()
if (applyInitialTheme) {
  applyInitialTheme()
}
// Arm the Monet background-extraction watcher (idempotent singleton).
useDynamicPalette()

// Global error containment: logs every uncaught error and, on the first fatal
// one, swaps the stuck splash for a recoverable DOM fallback instead of leaving
// a silent solid-color window. Installed before mount so render errors during
// boot are also caught.
const report = installRendererErrorFallback(app)
try {
  app.mount('#app')
  // The main process restores the saved login during this first call; without it the sidebar
  // showed "not signed in" until the user opened Settings → Personal.
  void auth.refreshAuthState()
} catch (err) {
  // A throw at mount means nothing rendered — this is fatal, show the fallback.
  report(err, { source: 'app.mount' }, true)
}

