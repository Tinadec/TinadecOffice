<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { onBeforeRouteLeave, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { UieShell } from '@tinadec/ui'
import { homeController } from '@/controllers/HomeController'
import { consumeRequest, pendingConversationId, pendingProjectId } from '@/lib/pageRequests'
import { useUiePage } from '@/lib/uiEngine'
import { useHomeEntrance } from '@/composables/useHomeEntrance'
import { useNotifications } from '@/composables/useNotifications'
import { selectionKey } from '@/lib/storageScope'
import { closeBuddyView } from '@/lib/buddyView'

// The UIE store is a module singleton shared by every UIE route; entering Home
// switches it back to the home layout (restoring the user's saved home layout).
const uie = useUiePage('home')
const router = useRouter()
homeController.setViewMode('flat')
const emit = defineEmits<{ ready: [] }>()
const root = ref<HTMLElement | null>(null)
const { contentReady, phase } = useHomeEntrance(root, uie.ready, () => emit('ready'))

// Spatial transition state — declarative, class-driven.
// Same mechanism as the Settings page: toggling container classes
// (.home-entering / .home-exiting) drives CSS @keyframes in
// page-transitions.css. No <Transition>, no v-show, no document.querySelector.
//
// Keep transforms on the material roots, never their ancestors; the UIE store
// owns geometry and the page wrapper only supplies declarative animation state.
//
// Entry animation: the container is always visible (never display:none), so
// UieCanvas.measure() reads the real .uie-canvas size on the first frame
// and the columns get correct geometry. The .home-entering class then plays
// the rise-from-below keyframes on the freshly mounted stacks — on both
// initial load and when returning from settings (fresh stacks replay the
// keyframes, exactly like the Settings nav/content do on every mount).
const homeExiting = ref(false)

// Exit duration must match page-transitions.css (@keyframes home-up-exit:
// 0.2s per column + 0.1s max stagger delay).
const EXIT_DURATION_MS = 300
const controllerStarted = ref(false)
const { t } = useI18n()
const { notify } = useNotifications()

onMounted(() => {
  homeController.start()
  controllerStarted.value = true
})

// A spotlight conversation row hands its id over through the request channel:
// the palette navigates here, and the selection lands on the one ref the
// sidebar, the message pane and the loader all already read.
const requestedProjectId = ref<string | null>(null)
const requestedConversationId = ref<string | null>(null)
const refreshingNavigation = ref(false)
let navigationGeneration = 0
let refreshedGeneration = -1
consumeRequest(pendingProjectId, (projectId) => {
  navigationGeneration++
  requestedConversationId.value = null
  requestedProjectId.value = projectId
  // A search hit is a request for the work surface; leaving the buddy view up would hide it.
  closeBuddyView()
})
consumeRequest(pendingConversationId, (sessionId) => {
  navigationGeneration++
  requestedProjectId.value = null
  requestedConversationId.value = sessionId
  closeBuddyView()
})
watch([requestedProjectId, homeController.projects], () => {
  const projectId = requestedProjectId.value
  if (!projectId || !homeController.projects.value.some((project) => selectionKey(project) === projectId)) return
  requestedProjectId.value = null
  homeController.setSelectedProject(projectId)
}, { immediate: true })
watch([requestedConversationId, homeController.sessions], () => {
  const session = homeController.sessions.value.find((item) => selectionKey(item) === requestedConversationId.value)
  if (!session) return
  requestedConversationId.value = null
  homeController.setViewMode(session.view_mode ?? 'flat')
  homeController.setSelectedProject(session.project_id ? selectionKey({ id: session.project_id, storage_id: session.storage_id }) : null)
  homeController.setSelectedSession(selectionKey(session))
  if (session.view_mode === 'space') void router.push('/space')
}, { immediate: true })
watch([controllerStarted, homeController.busy, requestedProjectId, requestedConversationId, refreshingNavigation], () => {
  if (!controllerStarted.value || homeController.busy.value || refreshingNavigation.value) return
  const missingProject = requestedProjectId.value && !homeController.projects.value.some((item) => selectionKey(item) === requestedProjectId.value)
  const missingSession = requestedConversationId.value && !homeController.sessions.value.some((item) => selectionKey(item) === requestedConversationId.value)
  if (!missingProject && !missingSession) return
  if (refreshedGeneration !== navigationGeneration) {
    refreshedGeneration = navigationGeneration
    refreshingNavigation.value = true
    void homeController.refreshProjectsAndSessions()
      .catch(() => undefined)
      .finally(() => { refreshingNavigation.value = false })
    return
  }
  requestedProjectId.value = null
  requestedConversationId.value = null
  notify.error(t('app.loadFailedMessage'), { title: t('app.loadFailed'), key: 'search-navigation' })
})

// Spatial exit: toggle .home-exiting so the exit keyframes drive the staggered
// upward flight declaratively, then complete the navigation once the CSS
// animation settles. Mirrors SettingsPage.vue's onBeforeRouteLeave guard.
onBeforeRouteLeave((_to, _from, next) => {
  if (homeExiting.value) {
    next()
    return
  }
  homeExiting.value = true
  setTimeout(() => next(), EXIT_DURATION_MS)
})
</script>

<template>
  <div
    ref="root"
    class="home-page-container"
    :class="{ 'home-preparing': phase === 'preparing', 'home-entering': phase === 'entering', 'home-exiting': homeExiting }"
  >
    <UieShell v-if="contentReady && homeController.viewMode.value === 'flat'" />
  </div>
</template>

<style scoped>
.home-page-container {
  position: relative;
  width: 100%;
  height: 100%;
  overflow: hidden;
}
</style>
