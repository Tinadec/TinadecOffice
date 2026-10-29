<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { UieCanvas } from '@tinadec/ui'
import AppHeader from '@/components/AppHeader.vue'
import AppSidebar from '@/components/AppSidebar.vue'
import { requestSettingsSection } from '@/lib/pageRequests'
import { setFeedFilter } from '@/lib/companionStore'
import { useUiePage } from '@/lib/uiEngine'

const uie = useUiePage('companionCenter')
const emit = defineEmits<{ ready: [] }>()
const canvasReady = ref(false)
let alive = true

const router = useRouter()

// The sidebar collapse is this page's own chrome; the work pages keep theirs in the UIE snapshot.
const sidebarCollapsed = ref(false)

function openPersonal() {
  requestSettingsSection('personal')
  void router.push('/settings')
}

function openCompanionSettings() {
  requestSettingsSection('companions')
  void router.push('/settings')
}

/**
 * The companion nav only re-aims the centre. It never selects a work session or a project, which
 * is why this page no longer starts the home controller: the work domain is not read here at all.
 */
function handleCompanionNav(target: 'all' | 'attention' | 'settings') {
  if (target === 'settings') {
    openCompanionSettings()
    return
  }
  setFeedFilter(target === 'attention' ? 'attention' : 'all')
}

onMounted(async () => {
  await uie.ready
  if (!alive) return
  uie.showPage('companionCenter')
  canvasReady.value = true
  await nextTick()
  if (alive) emit('ready')
})

onBeforeUnmount(() => {
  alive = false
})
</script>

<template>
  <main class="shell">
    <div class="center-sidebar">
      <!-- Same component, same brand header, same footer as every work page. Only the middle
           differs: no projects, no sessions, no "new chat" — the companion domain's own entries. -->
      <AppSidebar
        :projects="[]"
        :sessions="[]"
        :selected-project-id="null"
        :selected-session-id="null"
        :busy="false"
        :collapsed="sidebarCollapsed"
        :companion-center-active="true"
        :companion-domain="true"
        @go-settings="router.push('/settings')"
        @go-personal="openPersonal"
        @go-market="router.push('/market')"
        @go-companion-center="router.push('/companion-center')"
        @companion-nav="handleCompanionNav"
        @change-view="router.push($event === 'space' ? '/space' : '/')"
        @toggle-collapse="sidebarCollapsed = !sidebarCollapsed"
      />
    </div>
    <div class="center-body">
      <div class="top-drag-bar" />
      <AppHeader />
      <UieCanvas v-if="canvasReady" />
    </div>
  </main>
</template>

<style scoped>
.shell {
  position: relative;
  height: 100vh;
  display: flex;
  min-height: 0;
  overflow: hidden;
  background: transparent;
}

.center-sidebar {
  flex: none;
  width: 264px;
  height: 100%;
  min-height: 0;
  display: flex;
  overflow: hidden;
}

.center-sidebar:has(.sidebar-collapsed) {
  width: 56px;
}

.center-body {
  position: relative;
  flex: 1;
  min-width: 0;
  height: 100vh;
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
  background: transparent;
}
</style>
