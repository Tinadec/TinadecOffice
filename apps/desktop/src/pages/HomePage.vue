<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { UieShell } from '@tinadec/ui'
import { homeController } from '@/controllers/HomeController'
import { consumeRequest, pendingConversationId, pendingProjectId } from '@/lib/pageRequests'
import { ensureProductionUie } from '@/lib/uiEngine'
import { useHomeEntrance } from '@/composables/useHomeEntrance'
import { usePageTransition } from '@/composables/usePageTransition'
import { useNotifications } from '@/composables/useNotifications'
import { selectionKey } from '@/lib/storageScope'
import {
  activeDockFocus,
  ensureHomeDockCardOpen,
  focusDockCard,
  highlightedDockCard,
  type ApprovalDockTab,
  type HomeDockCardId,
} from '@/lib/homeDockSync'

// The UIE store is a module singleton shared by every UIE route; HomePage drives
// page switching across `'home'`, `'board'`, and `'projectHub'` from the active child route.
const uie = ensureProductionUie()
const router = useRouter()
homeController.setViewMode('flat')
const emit = defineEmits<{ ready: [] }>()
const root = ref<HTMLElement | null>(null)

const currentHomeSurface = computed<'chat' | 'board' | 'project-hub'>(() => {
  const current = router?.currentRoute?.value
  if (!current) return 'chat'
  if (current.meta?.homeSurface === 'board' || current.name === 'board' || current.path === '/board') {
    return 'board'
  }
  if (
    current.meta?.homeSurface === 'project-hub' ||
    current.name === 'project-hub' ||
    (typeof current.path === 'string' && current.path.startsWith('/projects/'))
  ) {
    return 'project-hub'
  }
  return 'chat'
})

const targetUiePageId = computed<'home' | 'board' | 'projectHub'>(() => {
  if (currentHomeSurface.value === 'board') return 'board'
  if (currentHomeSurface.value === 'project-hub') return 'projectHub'
  return 'home'
})

watch(
  targetUiePageId,
  (pageId) => {
    uie.showPage(pageId)
  },
  { immediate: true },
)

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
const { isExiting: homeExiting } = usePageTransition({
  skipEnterLifecycle: true,
  skipExitWhen: () => !contentReady.value || homeController.viewMode.value !== 'flat',
})
const pageTransitionClasses = computed(() => {
  const exiting = homeExiting.value
  const currentPhase = phase.value as string
  const preparing = !exiting && currentPhase === 'preparing'
  const entering = !exiting && currentPhase === 'entering'
  const idle = !exiting && !preparing && !entering
  return {
    'home-preparing': preparing,
    'home-entering': entering,
    'home-exiting': exiting,
    'route-preparing': preparing,
    'route-entering': entering,
    'route-idle': idle,
    'route-exiting': exiting,
  }
})
const controllerStarted = ref(false)
const { t } = useI18n()
const { notify } = useNotifications()

async function onFocusHomeDockEvent(event: Event): Promise<void> {
  const detail = (event as CustomEvent<{
    card?: HomeDockCardId
    approvalTab?: ApprovalDockTab
    subView?: 'timeline' | 'trajectory'
    runId?: string | null
  }>).detail
  if (!detail?.card) return
  if (targetUiePageId.value !== 'home') {
    if (router?.currentRoute?.value && router.currentRoute.value.path !== '/') {
      await router.push('/')
    }
    if ((targetUiePageId.value as string) !== 'home') return
  }
  focusDockCard({
    card: detail.card,
    ...(detail.approvalTab ? { approvalTab: detail.approvalTab } : {}),
    ...(detail.runId ? { runId: detail.runId } : {}),
  }, { uie, title: dockCardTitle(detail.card) })

  if (detail.card === 'orchestration' && detail.subView) {
    const subView = detail.subView
    void nextTick(() => {
      setTimeout(() => {
        const container = root.value ?? document
        if (subView === 'timeline') {
          const timelineBtn = container.querySelector<HTMLButtonElement>('[data-testid="topology-view-btn-timeline"]')
          timelineBtn?.click()
          timelineBtn?.scrollIntoView?.({ behavior: 'smooth', block: 'nearest' })
        } else if (subView === 'trajectory') {
          const trajBlock = container.querySelector<HTMLElement>('[data-testid="trajectory-diagnostics-block"]')
          trajBlock?.scrollIntoView?.({ behavior: 'smooth', block: 'start' })
        }
      }, 60)
    })
  }
}

function onLiveDigestFillSteerEvent(event: Event): void {
  const detail = (event as CustomEvent<{ prompt?: string | null }>).detail
  const prompt = typeof detail?.prompt === 'string' ? detail.prompt.trim() : ''
  if (!prompt) return
  homeController.updateDraft(prompt)
  if (router?.currentRoute?.value && router.currentRoute.value.path !== '/') {
    void router.push('/')
  }
  window.dispatchEvent(new CustomEvent('tinadec:focus-composer', { detail: { draft: prompt } }))
  void nextTick(() => {
    const container = root.value ?? document
    const textarea = container.querySelector<HTMLTextAreaElement>(
      'textarea.welcome-dialog-input, textarea[data-testid="composer-input"], textarea[data-testid="composer-textarea"], .composer-box textarea, .composer-textarea',
    )
    if (textarea) {
      textarea.focus()
      const end = textarea.value.length
      try {
        textarea.setSelectionRange(end, end)
      } catch {
        // Non-selectable element in isolated test environments.
      }
    }
  })
}

onMounted(() => {
  uie.showPage(targetUiePageId.value)
  homeController.start()
  controllerStarted.value = true
  window.addEventListener('tinadec:focus-home-dock', onFocusHomeDockEvent)
  window.addEventListener('tinadec:live-digest-fill-steer', onLiveDigestFillSteerEvent)
  if (activeDockFocus.value && targetUiePageId.value === 'home') {
    ensureHomeDockCardOpen(activeDockFocus.value.card, { uie, title: dockCardTitle(activeDockFocus.value.card) })
  }
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
})
consumeRequest(pendingConversationId, (sessionId) => {
  navigationGeneration++
  requestedProjectId.value = null
  requestedConversationId.value = sessionId
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

const focusedWorkerAttr = computed(() => {
  const focus = activeDockFocus.value
  return focus?.workerHandle ?? focus?.taskId ?? focus?.workerLabel ?? undefined
})

function dockCardTitle(card: HomeDockCardId): string {
  return t(`uie.cardTitle.${card}`)
}

let highlightPulseTimer: ReturnType<typeof setTimeout> | null = null
let activeHighlightHost: HTMLElement | null = null

function clearHostHighlight(): void {
  if (highlightPulseTimer !== null) {
    clearTimeout(highlightPulseTimer)
    highlightPulseTimer = null
  }
  if (activeHighlightHost) {
    activeHighlightHost.classList.remove('is-dock-focus-highlight')
    activeHighlightHost = null
  }
}

onUnmounted(() => {
  clearHostHighlight()
  window.removeEventListener('tinadec:focus-home-dock', onFocusHomeDockEvent)
  window.removeEventListener('tinadec:live-digest-fill-steer', onLiveDigestFillSteerEvent)
})

watch(
  () => activeDockFocus.value?.nonce,
  async () => {
    const focus = activeDockFocus.value
    if (!focus || targetUiePageId.value !== 'home') return
    ensureHomeDockCardOpen(focus.card, { uie, title: dockCardTitle(focus.card) })
    await nextTick()
    const container = root.value
    if (!container) return

    clearHostHighlight()
    const explicitCard = container.querySelector<HTMLElement>(`[data-dock-card="${focus.card}"]`)
    const host = explicitCard?.closest<HTMLElement>('.uie-card-host')
      ?? container.querySelector<HTMLElement>('.uie-slot-right .uie-card-host:not(.uie-card-host--hidden)')
      ?? container.querySelector<HTMLElement>('.uie-card-host:not(.uie-card-host--hidden)')
    if (host) {
      host.classList.add('is-dock-focus-highlight')
      activeHighlightHost = host
      highlightPulseTimer = setTimeout(() => {
        host.classList.remove('is-dock-focus-highlight')
        if (activeHighlightHost === host) activeHighlightHost = null
        highlightPulseTimer = null
      }, 2200)
    }

    const searchRoot = explicitCard ?? host ?? container
    const selectors: string[] = []
    if (focus.taskId) selectors.push(`[data-worker-task-id="${focus.taskId}"]`, `[data-task-id="${focus.taskId}"]`)
    if (focus.approvalId) selectors.push(`[data-approval-id="${focus.approvalId}"]`)
    if (focus.worktreePath) selectors.push(`[data-worktree-path="${focus.worktreePath.replace(/"/g, '\\"')}"]`)
    for (const selector of selectors) {
      const targetEl = searchRoot.querySelector<HTMLElement>(selector)
      if (targetEl) {
        if (typeof targetEl.scrollIntoView === 'function') {
          targetEl.scrollIntoView({ behavior: 'smooth', block: 'nearest' })
        }
        break
      }
    }
  },
)

</script>

<template>
  <div
    ref="root"
    class="home-page-container"
    :class="pageTransitionClasses"
    :data-home-surface="currentHomeSurface"
    :data-dock-focused-card="highlightedDockCard ?? undefined"
    :data-dock-focused-worker="focusedWorkerAttr"
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

.home-page-container :deep(.uie-card-host.is-dock-focus-highlight),
.home-page-container :deep([data-dock-highlighted='true']) {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 75%, transparent);
  outline-offset: -2px;
  border-radius: 10px;
  box-shadow: 0 0 0 4px color-mix(in srgb, var(--accent-primary) 18%, transparent);
  animation: home-dock-pulse 1.2s ease-out;
}

@keyframes home-dock-pulse {
  0% {
    box-shadow: 0 0 0 0 color-mix(in srgb, var(--accent-primary) 42%, transparent);
  }
  100% {
    box-shadow: 0 0 0 4px color-mix(in srgb, var(--accent-primary) 18%, transparent);
  }
}

@media (prefers-reduced-motion: reduce) {
  .home-page-container :deep(.uie-card-host.is-dock-focus-highlight),
  .home-page-container :deep([data-dock-highlighted='true']) {
    animation: none;
  }
}
</style>
