<script setup lang="ts">
import { RouterView, useRoute, useRouter } from 'vue-router'
import { computed, watch, onMounted, onBeforeUnmount, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useBackground } from '@/composables/useBackground'
import {
  useConnection,
  retryConnection,
} from '@/composables/useConnection'
import { startStatusSync } from '@/composables/useNotifications'
import { initPlatform } from '@/composables/usePlatform'
import { setHostAccessStatus, useHostAccess } from '@/lib/hostAccess'
import { ensureGraphSeedPack, setGraphSeedPackTranslator } from '@/agentPacks/graphSeedPackBootstrap'
import { evaluateOobeAdmission } from '@/composables/useOobe'
import AppSplash from '@/components/AppSplash.vue'
import NotificationIslandHost from '@/components/NotificationIslandHost.vue'
import NotificationDetailDialog from '@/components/NotificationDetailDialog.vue'
import SelectionContextMenu from '@/components/SelectionContextMenu.vue'
import CommandPalette from '@/components/CommandPalette.vue'
import OobeWizardModal from '@/components/oobe/OobeWizardModal.vue'
import RecoveryCheckDialog from '@/components/governance/RecoveryCheckDialog.vue'
import WebLoginPage from '@/components/web/WebLoginPage.vue'
import ServerDirectoryPickerModal from '@/components/web/ServerDirectoryPickerModal.vue'
import { installPaletteKeybinding } from '@/composables/useCommandPalette'
import { selectCompanion } from '@/lib/companionStore'
import { homeController } from '@/controllers/HomeController'

// ---- Background layer (global, outside page transitions) ----
// The background layer is ALWAYS rendered here — outside the <Transition> —
// so it is never affected by page-transition transforms.  CSS `position: fixed`
// inside a transformed ancestor behaves like `position: absolute`, which
// would cause the background to slide along with the page.  By keeping it
// here, the background stays perfectly static during navigation.
//
// Even when type === 'none' (no custom background), the layer is still
// rendered with the theme's --bg-primary colour.  This ensures the window
// has a stable, non-animated bottom layer at all times.  Page containers
// (.shell, .settings-page, .workspace) are always transparent so this
// layer shows through.
const { settings: backgroundSettings, applyBackground } = useBackground()
watch(backgroundSettings, () => applyBackground(), { deep: true, immediate: true })

// ---- Backend connection gating ----
// The shell mounts on the first frame, whatever the backend is doing. Main no longer blocks the
// window on backend startup, so waiting for "connected" here would park the splash for up to the
// 30 s timeout on a slow or broken host. What the user gets instead is the real interface plus
// NotificationIslandHost (`backend-connection` status capsule), which names the state and offers
// the retry even during cold start. Business reads stay gated on host access (useHostAccess /
// assertHostAccess), and the splash leaves when the mounted route reports its prepared frame.
// 子窗口（?splash=0，如 Debug Studio / Detached Panel）跳过 splash：
// 它们复用主窗口已建立的后端连接，不应重播首次启动序列。
const isChildWindow = new URLSearchParams(window.location.search).get('splash') === '0'
const isPetWindow = window.location.hash.startsWith('#/pet')
// The companion capsule/panel is its own always-on-top window: transparent, no splash, no
// backend coupling of its own. It shares this bundle only, so it gets the bare RouterView.
// The center page's path also begins with "/companion", so this compares the whole hash path:
// a prefix test would strand `/companion-center` in the satellite window and skip the backend
// connection the main shell needs.
function hashRoutePath(hash: string): string {
  const query = hash.indexOf('?')
  return query === -1 ? hash : hash.slice(0, query)
}
const isCompanionWindow = hashRoutePath(window.location.hash) === '#/companion'
const isStandaloneWindow = isPetWindow || isCompanionWindow
const { t } = useI18n()
setGraphSeedPackTranslator((key, params) => String(t(key, params ?? {})))
const {
  connectionState,
  hostStatus,
  businessReady,
  start: startConnection,
  syncConnectionNotifications,
  clearConnectionNotificationTimers,
} = useConnection()
let graphSeedBootstrapped = false
let graphSeedBootstrapping = false
let unsubscribeStatusSync: (() => void) | undefined
let uninstallPaletteKeys: (() => void) | undefined
let unsubscribeFocusSession: (() => void) | undefined
let unsubscribeNavigateSurface: (() => void) | undefined
const disposePlatform = initPlatform()
const route = useRoute()
const router = useRouter()
// Startup happens once. Retrying a lost backend must preserve the live page.
const entryReady = ref(isChildWindow)
const recoveryDialogOpen = ref(false)
const recoveryActionId = ref<string | null>(null)

function onOpenRecoveryCheck(event: Event) {
  const detail = (event as CustomEvent<{ actionId?: string }>).detail
  if (!detail?.actionId) return
  recoveryActionId.value = detail.actionId
  recoveryDialogOpen.value = true
}

// The gate starts empty, and an empty gate reads as "no host has refused anything yet" — every
// business read would then go out before the host was verified. Publishing "checking" here (App's
// setup runs before the routed page's) keeps those reads closed until useConnection reports the real
// state. A preview renderer already published "preview" before mount, so it keeps its own reason.
const hostAccess = useHostAccess()
const { status: hostAccessStatus } = hostAccess
const isAuthRequired = computed(
  () => hostAccess.isAuthRequired?.value ?? hostAccessStatus.value?.state === 'auth_required',
)
const isElectron = computed(() => {
  if (hostAccess.isElectron) return hostAccess.isElectron.value
  if (typeof window === 'undefined') return false
  const bridge = window.tinadec as (typeof window.tinadec & { isWebShim?: boolean }) | undefined
  return Boolean(
    bridge &&
      !bridge.isWebShim &&
      hostAccessStatus.value?.state !== 'preview' &&
      typeof bridge.minimizeWindow === 'function',
  )
})
if (!isStandaloneWindow && hostAccessStatus.value === null) {
  setHostAccessStatus({ state: 'checking', managed: true })
}

watch(
  isElectron,
  (electron) => {
    if (typeof document !== 'undefined') {
      document.documentElement.dataset.tinadecPlatform = electron ? 'electron' : 'web'
    }
  },
  { immediate: true },
)

function onPageReady() {
  entryReady.value = true
}

function onRouteMounted() {
  // UIE routes report readiness after selecting and measuring their layouts.
  // Other routes have their own entry animations and are ready on mount.
  if (
    !route.meta?.homeSurface &&
    route.name !== 'home' &&
    route.name !== 'board' &&
    route.name !== 'project-hub' &&
    route.name !== 'market' &&
    route.name !== 'marketSkills' &&
    route.name !== 'marketMcp' &&
    route.name !== 'space'
  ) {
    onPageReady()
  }
}

// OOBE admission runs ONLY after AppSplash has left (entryReady) AND backend is connected.
watch(
  [entryReady, businessReady],
  ([ready, backendReady]) => {
    if (isStandaloneWindow || isChildWindow) return
    if (ready && backendReady) {
      void evaluateOobeAdmission()
    }
  },
)

watch(
  [businessReady, isAuthRequired, connectionState, hostStatus],
  ([ready, authRequired]) => {
    if (isStandaloneWindow || isChildWindow) return
    syncConnectionNotifications({ isAuthRequired: authRequired })
    if (ready && !authRequired && !graphSeedBootstrapped && !graphSeedBootstrapping) {
      graphSeedBootstrapping = true
      void ensureGraphSeedPack()
        .then((completed) => {
          if (completed) {
            graphSeedBootstrapped = true
          }
        })
        .finally(() => {
          graphSeedBootstrapping = false
        })
    }
  },
  { immediate: true },
)

onMounted(() => {
  if (!isStandaloneWindow) void startConnection()
  if (!isStandaloneWindow) {
    unsubscribeStatusSync = startStatusSync()
    // The palette is the window's command surface, and a pet has no commands to run.
    uninstallPaletteKeys = installPaletteKeybinding()
    // A session partner window asked to return: select its session and show the work surface.
    unsubscribeFocusSession = window.tinadec?.sessionPartner?.onFocusSession((sessionId) => {
      void homeController.setSelectedSession(sessionId)
      void router.push('/')
    })
    unsubscribeNavigateSurface = window.tinadec?.onNavigateSurface?.((payload) => {
      if (payload?.target === 'companion-center') {
        if (payload.companionId) {
          selectCompanion(payload.companionId)
        }
        void router.push({
          path: '/companion-center',
          ...(payload.companionId ? { query: { companionId: payload.companionId } } : {}),
        })
        return
      }
      if (payload?.target === 'session' && payload.sessionId) {
        try {
          sessionStorage.setItem('tinadec.pendingConversationId', payload.sessionId)
        } catch {
          // Ignore storage quota errors in restricted webviews.
        }
        void homeController.setSelectedSession(payload.sessionId)
        void router.push('/')
      }
    })
    window.addEventListener('tinadec:open-recovery-check', onOpenRecoveryCheck)
  }
})

onBeforeUnmount(() => {
  clearConnectionNotificationTimers()
  disposePlatform()
  unsubscribeStatusSync?.()
  uninstallPaletteKeys?.()
  unsubscribeFocusSession?.()
  unsubscribeNavigateSurface?.()
  window.removeEventListener('tinadec:open-recovery-check', onOpenRecoveryCheck)
})
</script>

<template>
  <RouterView v-if="isStandaloneWindow" />

  <template v-else>
  <!-- Keep one Vue splash node through route preparation and CSS departure.
       Home readiness includes saved layout, fonts and measured paint frames. -->
  <AppSplash
    v-if="!isChildWindow && !isPetWindow"
    :class="{ 'app-splash--leaving': entryReady }"
    :aria-hidden="entryReady ? 'true' : undefined"
  />

  <!-- Background Layer — rendered behind splash during route preparation.
       INTENTIONALLY OUTSIDE any <Transition> / transformed ancestor:
       CSS position:fixed degrades to absolute inside a transformed parent,
       which would make the background slide with the page (see comment below).
       This div is the stable, static foundation of the entire window. -->
  <div
    class="background-layer"
    :class="{ 'background-layer--none': backgroundSettings.type === 'none' }"
  >
    <!-- Image Background -->
    <div
      v-if="backgroundSettings.type === 'image'"
      class="background-image"
      :style="{
        backgroundImage: backgroundSettings.source ? `url('${backgroundSettings.source}')` : 'none',
        backgroundSize: backgroundSettings.size,
        backgroundPosition: backgroundSettings.position,
        backgroundRepeat: backgroundSettings.repeat,
        opacity: backgroundSettings.opacity / 100,
        filter: backgroundSettings.blur > 0 ? `blur(${backgroundSettings.blur}px)` : 'none',
      }"
    />
    <!-- Video Background -->
    <video
      v-else-if="backgroundSettings.type === 'video' && backgroundSettings.source"
      class="background-video"
      :src="backgroundSettings.source"
      autoplay
      loop
      muted
      :style="{
        opacity: backgroundSettings.opacity / 100,
        filter: backgroundSettings.blur > 0 ? `blur(${backgroundSettings.blur}px)` : 'none',
      }"
    />
    <!-- HTML Background -->
    <div
      v-else-if="backgroundSettings.type === 'html' && backgroundSettings.source"
      class="background-html"
      v-html="backgroundSettings.source"
      :style="{
        opacity: backgroundSettings.opacity / 100,
        filter: backgroundSettings.blur > 0 ? `blur(${backgroundSettings.blur}px)` : 'none',
      }"
    />
    <!-- When type === 'none', the layer is empty but still has --bg-primary
         from .background-layer--none CSS class. -->
  </div>

  <!-- Main content shell.
       页面入场动画由各页面（如 HomePage）内部触发，
       而非在此处包裹 RouterView —— 因为路由组件是懒加载的，外层 Transition
       会在子元素挂载前就移除 enter-active 类，导致动画失效。 -->
  <div class="main-content">
    <!-- No Transition wrapper around RouterView: TinadecUIE owns the main
         window layout and the stable card-frame material root. A transition
         would unload the page host on route change, breaking backdrop-filter
         and hitting removed-node patches with TinadecUIE's absolute layout. -->
    <RouterView v-slot="{ Component }">
      <component :is="Component" @ready="onPageReady" @vue:mounted="onRouteMounted" />
    </RouterView>
  </div>
  <NotificationIslandHost />
  <NotificationDetailDialog />
  <SelectionContextMenu />
  <CommandPalette />
  <RecoveryCheckDialog
    v-if="recoveryDialogOpen && !isChildWindow"
    v-model:open="recoveryDialogOpen"
    :action-id="recoveryActionId"
  />
  <OobeWizardModal v-if="entryReady && !isChildWindow && !isAuthRequired" />
  <ServerDirectoryPickerModal v-if="!isChildWindow" />
  <WebLoginPage v-if="isAuthRequired && !isChildWindow" @authenticated="retryConnection" />
  </template>
</template>

<style>
:root[data-tinadec-platform='web'] .window-btn:not(.commands),
:root[data-tinadec-platform='web'] .detached-titlebar-btn:not(.reattach-btn) {
  display: none !important;
}
</style>
