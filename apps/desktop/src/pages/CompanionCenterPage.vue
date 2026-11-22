<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import AppHeader from '@/components/AppHeader.vue'
import CompanionContactsRail from '@/components/companion/CompanionContactsRail.vue'
import CompanionThreadWorkspace from '@/components/companion/CompanionThreadWorkspace.vue'
import CompanionRightDock from '@/components/companion/CompanionRightDock.vue'
import CompanionEditorDrawer from '@/settings/sections/CompanionEditorDrawer.vue'
import { usePageTransition } from '@/composables/usePageTransition'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { useHostAccess } from '@/lib/hostAccess'
import { requestSettingsSection } from '@/lib/pageRequests'
import { companionApi, type CompanionAvatar } from '@/lib/companionApi'
import {
  autonomousCompanions,
  mainCompanion,
  selectedCompanion,
  selectedCompanionId,
  selectedConversationId,
  selectCompanion,
  useCompanionCenter,
} from '@/lib/companionStore'

const emit = defineEmits<{ ready: [] }>()
const router = useRouter()
const { transitionClasses } = usePageTransition()
const { canAccessBackend } = useHostAccess()
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
const pageMaterialStyle = computed(() => {
  const materialStyle = getPanelStyle()
  return {
    '--material-filter-section': materialStyle['--material-filter-section'] ?? 'none',
    '--material-filter-raised': materialStyle['--material-filter-raised'] ?? 'none',
  }
})
const panelDataAttrs = computed(() => getPanelDataAttributes())

const center = useCompanionCenter()
const live2dAvatars = ref<CompanionAvatar[]>([])
const live2dSlugs = computed(() => live2dAvatars.value.map((item) => item.id))

const drawerOpen = ref(false)
const drawerCompanionId = ref<string | null>(null)
const drawerInitialTab = ref<'identity' | 'memory' | 'skills' | 'connections' | 'autonomy'>('identity')

let alive = true

async function loadAvatars() {
  if (!canAccessBackend.value) return
  try {
    live2dAvatars.value = await companionApi.listCompanionAvatars()
  } catch {
    live2dAvatars.value = []
  }
}

watch(
  [autonomousCompanions, mainCompanion],
  ([bots, main]) => {
    if (selectedCompanionId.value || !bots.length) return
    selectCompanion((main ?? bots[0]!).id)
  },
  { immediate: true },
)

watch(canAccessBackend, (ready) => {
  if (ready) void loadAvatars()
}, { immediate: true })

onMounted(async () => {
  await nextTick()
  if (alive) emit('ready')
})

onBeforeUnmount(() => {
  alive = false
})

function openCreateDrawer() {
  drawerCompanionId.value = null
  drawerInitialTab.value = 'identity'
  drawerOpen.value = true
}

function openEditDrawer(companionId: string, tab: 'identity' | 'memory' | 'skills' | 'connections' | 'autonomy' = 'identity') {
  drawerCompanionId.value = companionId
  drawerInitialTab.value = tab
  drawerOpen.value = true
}

function onDrawerSaved(companion: { id: string }) {
  selectCompanion(companion.id)
  void center.refresh()
}

function openSettings() {
  requestSettingsSection('companions')
  void router.push('/settings')
}
</script>

<template>
  <div
    class="companion-center-page"
    :class="transitionClasses"
    :style="pageMaterialStyle"
    v-bind="panelDataAttrs"
    data-testid="companion-center-workspace"
  >
    <div class="top-drag-bar" />
    <AppHeader />

    <div class="companion-center-shell">
      <CompanionContactsRail
        class="companion-center-rail"
        :live2d-slugs="live2dSlugs"
        @create-companion="openCreateDrawer"
        @edit-companion="openEditDrawer($event)"
        @go-home="router.push('/')"
        @go-space="router.push('/space')"
        @go-settings="openSettings"
      />

      <div class="companion-center-body">
        <CompanionThreadWorkspace
          :companion="selectedCompanion"
          :conversation-id="selectedConversationId"
          :live2d-slugs="live2dSlugs"
          @edit-companion="openEditDrawer($event)"
        />

        <CompanionRightDock
          :companion="selectedCompanion"
          :conversation-id="selectedConversationId"
          :live2d-slugs="live2dSlugs"
          @edit-companion="(id, tab) => openEditDrawer(id, tab)"
        />
      </div>
    </div>

    <CompanionEditorDrawer
      :open="drawerOpen"
      :companion-id="drawerCompanionId"
      :initial-tab="drawerInitialTab"
      :live2d-avatars="live2dAvatars"
      @update:open="drawerOpen = $event"
      @close="drawerOpen = false"
      @created="onDrawerSaved"
      @saved="onDrawerSaved"
    />
  </div>
</template>

<style scoped>
.companion-center-page {
  --radius-island: 12px;
  position: relative;
  width: 100%;
  height: 100vh;
  overflow: hidden;
  background: transparent;
}

.companion-center-shell {
  display: grid;
  grid-template-columns: 270px minmax(0, 1fr);
  gap: 8px;
  width: 100%;
  height: 100%;
  padding: 8px;
  box-sizing: border-box;
  overflow: hidden;
}

.companion-center-rail {
  min-height: 0;
  height: 100%;
  border-radius: var(--radius-island, 12px);
}

.companion-center-body {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 380px;
  gap: 8px;
  margin-top: 40px;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}

.companion-center-body :deep(.companion-thread),
.companion-center-body :deep(.companion-dock) {
  border-radius: var(--radius-island, 12px);
}

@media (max-width: 1120px) {
  .companion-center-shell {
    grid-template-columns: 240px minmax(0, 1fr);
  }

  .companion-center-body {
    grid-template-columns: minmax(0, 1fr) 320px;
  }
}
</style>
