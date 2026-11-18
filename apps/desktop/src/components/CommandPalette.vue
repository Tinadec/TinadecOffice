<script setup lang="ts">
import {
  Search, X, FolderKanban, MessageSquare, BrainCircuit, Bot, Workflow,
  FileText, FileCode2, FileJson, Image, Wrench, Settings2, Command,
  Layers3, ChevronDown, ChevronRight, ArrowUpRight, Maximize2, Minimize2, LoaderCircle,
  TriangleAlert, RefreshCw,
} from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { computed, nextTick, onBeforeUnmount, ref, watch, type Component } from 'vue'
import { useRouter } from 'vue-router'
import { UiButton } from '@/components/ui'
import AppHeader from '@/components/AppHeader.vue'
import { homeController } from '@/controllers/HomeController'
import { codeController } from '@/controllers/CodeController'
import { selectionKey } from '@/lib/storageScope'
import { closePalette, useCommandPalette } from '@/composables/useCommandPalette'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { availableCommands, implicitArgument, type AppCommand, type CommandHost } from '@/lib/appCommands'
import { toggleSessionPartner as togglePartnerWindow } from '@/lib/sessionPartner'
import {
  requestConversation, requestModelProvider, requestSettingsSection,
  requestProject, requestAgent, requestMode, requestPrompt, requestTool, requestWorkspaceFile,
} from '@/lib/pageRequests'
import {
  refreshSpotlight, searchSpotlight, spotlightKindLabel, spotlightKindOrder,
  type SpotlightGroup, type SpotlightHost, type SpotlightItem, type SpotlightKind,
} from '@/lib/spotlight'

const { t } = useI18n()
const router = useRouter()
const { open, comboLabel, seededQuery } = useCommandPalette()
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
const materialStyle = computed(() => getPanelStyle())
const materialAttributes = computed(() => getPanelDataAttributes())
const query = ref('')
const activeIndex = ref(0)
const activeKind = ref<SpotlightKind | 'all'>('all')
const groups = ref<SpotlightGroup[]>([])
const searching = ref(false)
const searchFailed = ref(false)
const fullscreen = ref(false)
const expandedGroups = ref(new Set<SpotlightKind>())
const collapsedGroups = ref(new Set<SpotlightKind>())
const dialogRef = ref<HTMLDialogElement | null>(null)
const inputRef = ref<HTMLInputElement | null>(null)
const PREVIEW_LIMIT = 4
const listId = 'command-palette-list'

const kindIcons: Record<SpotlightKind, Component> = {
  command: Command, project: FolderKanban, conversation: MessageSquare,
  model: BrainCircuit, agent: Bot, mode: Workflow, prompt: FileText,
  tool: Wrench, setting: Settings2, resource: FileCode2,
}

function resultIcon(item: SpotlightItem): Component {
  if (item.kind !== 'resource') return kindIcons[item.kind]
  const extension = item.label.split('.').pop()?.toLowerCase()
  if (extension && ['png', 'jpg', 'jpeg', 'webp', 'svg', 'gif'].includes(extension)) return Image
  if (extension === 'json') return FileJson
  if (extension && ['md', 'txt', 'log'].includes(extension)) return FileText
  return FileCode2
}

const commandHost: CommandHost = {
  canStop: () => Boolean(homeController.stoppableRunId.value),
  draft: () => homeController.draft.value,
  setDraft: (value) => homeController.updateDraft(value),
  send: (text, dispatch) => {
    homeController.updateDraft(text)
    void homeController.sendMessage({ dispatch_mode: dispatch, target_run_id: null })
  },
  stopRun: () => { void homeController.stopRun() },
  newSession: () => { void homeController.createSession(homeController.selectedProjectId.value ?? null) },
  navigate: (routeName) => { void router.push({ name: routeName }) },
  routeName: () => String(router.currentRoute.value.name ?? ''),
  toggleSessionPartner: () => { void togglePartnerWindow(homeController.selectedSessionId.value) },
}

function runCommand(command: AppCommand) {
  const argument = implicitArgument(command, commandHost)
  closePalette()
  command.run(commandHost, argument)
}

const commandItems = computed<SpotlightItem[]>(() => availableCommands(commandHost).map((command) => ({
  id: command.id, kind: 'command', label: t(command.labelKey),
  detail: command.slash ? `/${command.slash}` : undefined,
  keywords: (command.keywordKeys ?? []).map((key) => t(key)).join(' '),
  action: () => runCommand(command),
})))

function navigate(routeName: string) {
  if (router.currentRoute.value.name !== routeName) void router.push({ name: routeName })
}
const searchProject = computed(() => router.currentRoute.value.name === 'code-editor'
  ? codeController.currentProject.value
  : homeController.currentProject.value)
const spotlightHost: SpotlightHost = {
  navigate,
  navigateSettings: (section) => { requestSettingsSection(section); navigate('settings') },
  openSession: (id) => { requestConversation(id); navigate('home') },
  openProject: (id) => { requestProject(id); navigate('home') },
  openAgent: (id) => { requestAgent(id); navigate('settings') },
  openMode: (id) => { requestMode(id); navigate('settings') },
  openPrompt: (id) => { requestPrompt(id); navigate('settings') },
  openTool: (id) => { requestTool(id); navigate('settings') },
  selectProvider: (id) => { requestModelProvider(id); navigate('settings') },
  openWorkspacePath: (path, projectId) => {
    requestWorkspaceFile({ path, projectId })
    navigate('code-editor')
  },
  loadedSessions: () => homeController.sessions.value,
  workspaceRoot: () => searchProject.value?.path ?? '',
  workspaceProjectId: () => searchProject.value ? selectionKey(searchProject.value) : undefined,
}

const filteredGroups = computed(() => groups.value.filter((group) => activeKind.value === 'all' || group.kind === activeKind.value))
const visibleGroups = computed(() => filteredGroups.value.map((group) => ({
  ...group,
  visibleItems: collapsedGroups.value.has(group.kind) ? [] : expandedGroups.value.has(group.kind) ? group.items : group.items.slice(0, PREVIEW_LIMIT),
})))
const rows = computed(() => visibleGroups.value.flatMap((group) => group.visibleItems))
const totalResults = computed(() => filteredGroups.value.reduce((count, group) => count + group.items.length, 0))
const activeId = computed(() => rows.value.length ? `command-palette-option-${activeIndex.value}` : undefined)
const listIds = computed(() => visibleGroups.value.filter((g) => g.visibleItems.length).map((g) => `palette-results-${g.kind}`).join(' ') || undefined)

function countFor(kind: SpotlightKind) { return groups.value.find((group) => group.kind === kind)?.items.length ?? 0 }
function rowIndex(groupIndex: number, itemIndex: number) {
  return visibleGroups.value.slice(0, groupIndex).reduce((count, group) => count + group.visibleItems.length, 0) + itemIndex
}
function toggleGroup(kind: SpotlightKind) {
  const next = new Set(collapsedGroups.value)
  next.has(kind) ? next.delete(kind) : next.add(kind)
  collapsedGroups.value = next
  activeIndex.value = 0
}
function toggleMore(kind: SpotlightKind) {
  const next = new Set(expandedGroups.value)
  next.has(kind) ? next.delete(kind) : next.add(kind)
  expandedGroups.value = next
  activeIndex.value = 0
}
function selectKind(kind: SpotlightKind | 'all') {
  activeKind.value = kind
  activeIndex.value = 0
}

let searchToken = 0
let searchTimer: ReturnType<typeof setTimeout> | null = null
let searchAbort: AbortController | null = null
function cancelPendingSearch() {
  searchToken++
  searchAbort?.abort()
  searchAbort = null
  if (searchTimer) clearTimeout(searchTimer)
  searchTimer = null
}
function updateGroups(next: SpotlightGroup[]) {
  const selected = rows.value[activeIndex.value]?.id
  groups.value = next
  const index = selected ? rows.value.findIndex((item) => item.id === selected) : -1
  activeIndex.value = index >= 0 ? index : 0
}
async function runSearch() {
  if (!open.value) return
  searchAbort?.abort()
  const controller = new AbortController()
  searchAbort = controller
  const token = ++searchToken
  searching.value = true
  searchFailed.value = false
  try {
    const result = await searchSpotlight(query.value, spotlightHost, (key) => t(key), commandItems.value, {
      signal: controller.signal,
      onUpdate: (partial) => { if (token === searchToken && open.value) updateGroups(partial) },
    })
    if (token !== searchToken || !open.value) return
    updateGroups(result)
  } catch {
    if (token !== searchToken || !open.value) return
    groups.value = []
    searchFailed.value = true
  } finally {
    if (token === searchToken && open.value) searching.value = false
  }
}
watch(query, () => {
  cancelPendingSearch()
  groups.value = [] // Enter must never activate a result from the previous query.
  activeIndex.value = 0
  expandedGroups.value = new Set()
  collapsedGroups.value = new Set()
  if (!open.value) return
  searching.value = true
  searchTimer = setTimeout(() => { searchTimer = null; void runSearch() }, 180)
})
watch(commandItems, () => { if (open.value) void runSearch() })
watch(rows, (list) => { activeIndex.value = Math.min(activeIndex.value, Math.max(0, list.length - 1)) })
watch([open, dialogRef], async ([isOpen, element]) => {
  if (!element) return
  cancelPendingSearch()
  if (!isOpen) { if (element.open) element.close(); return }
  query.value = seededQuery.value
  activeIndex.value = 0
  activeKind.value = 'all'
  fullscreen.value = false
  expandedGroups.value = new Set()
  collapsedGroups.value = new Set()
  groups.value = []
  refreshSpotlight()
  element.showModal()
  await nextTick()
  if (!open.value) return
  inputRef.value?.focus()
  cancelPendingSearch()
  void runSearch()
})
watch(activeIndex, async () => {
  await nextTick()
  if (activeId.value) document.getElementById(activeId.value)?.scrollIntoView?.({ block: 'nearest' })
})
onBeforeUnmount(() => { cancelPendingSearch(); if (dialogRef.value?.open) dialogRef.value.close() })

function move(delta: number) {
  if (rows.value.length) activeIndex.value = (activeIndex.value + delta + rows.value.length) % rows.value.length
}
function run(item: SpotlightItem) { closePalette(); item.action() }
function onKeydown(event: KeyboardEvent) {
  if (event.target !== inputRef.value || event.isComposing) return
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault()
    move(event.key === 'ArrowDown' ? 1 : -1)
  } else if (event.key === 'Enter') {
    const item = rows.value[activeIndex.value]
    if (item) { event.preventDefault(); run(item) }
  }
}
</script>

<template>
  <dialog
    ref="dialogRef"
    class="command-palette no-drag"
    :class="{ 'command-palette--fullscreen': fullscreen }"
    :style="materialStyle"
    v-bind="materialAttributes"
    aria-labelledby="palette-title"
    aria-describedby="palette-description"
    @close="closePalette()"
    @cancel.prevent="closePalette()"
    @keydown="onKeydown"
  >
    <div v-if="fullscreen" class="top-drag-bar" />
    <AppHeader v-if="fullscreen" :show-search="false" />
    <div class="search-screen">
      <header class="search-screen-header sr-only">
        <div class="search-heading">
          <h1 id="palette-title">{{ t('palette.title') }}</h1>
          <p id="palette-description">{{ t('palette.description') }}</p>
        </div>
      </header>

      <div class="command-palette-search">
        <Search class="command-palette-search-icon" :size="24" aria-hidden="true" />
        <input
          ref="inputRef" v-model="query" class="command-palette-input" type="search" role="combobox"
          :placeholder="t('palette.spotlightPlaceholder')" :aria-label="t('palette.title')"
          aria-expanded="true" aria-autocomplete="list" :aria-controls="listIds"
          :aria-activedescendant="activeId" autocapitalize="off" autocomplete="off" spellcheck="false"
          data-testid="palette-input"
        />
        <UiButton v-if="query" variant="ghost" size="icon" class="search-action" :aria-label="t('palette.clear')" @click="query = ''; inputRef?.focus()">
          <X data-icon="inline-start" aria-hidden="true" />
        </UiButton>
        <kbd class="command-palette-accelerator">{{ comboLabel }}</kbd>
        <UiButton variant="ghost" size="icon" class="search-action" data-testid="palette-fullscreen"
          :aria-label="t(fullscreen ? 'palette.restoreSearch' : 'palette.expandSearch')"
          :title="t(fullscreen ? 'palette.restoreSearch' : 'palette.expandSearch')"
          :aria-pressed="fullscreen" @click="fullscreen = !fullscreen">
          <component :is="fullscreen ? Minimize2 : Maximize2" data-icon="inline-start" aria-hidden="true" />
        </UiButton>
        <UiButton variant="ghost" size="icon" class="search-action" data-testid="palette-close"
          :aria-label="t('palette.close')" :title="t('palette.close')" @click="closePalette()">
          <X data-icon="inline-start" aria-hidden="true" />
        </UiButton>
      </div>

      <div class="search-screen-body">
        <nav class="search-categories" :aria-label="t('palette.categories')" data-testid="palette-categories">
          <button class="search-category" :class="{ 'is-selected': activeKind === 'all' }" :aria-pressed="activeKind === 'all'" data-testid="palette-filter-all" @click="selectKind('all')">
            <Layers3 :size="18" aria-hidden="true" /><span>{{ t('palette.all') }}</span>
            <span class="search-category-count">{{ groups.reduce((count, group) => count + group.items.length, 0) }}</span>
          </button>
          <button
            v-for="kind in spotlightKindOrder" :key="kind" class="search-category"
            :class="{ 'is-selected': activeKind === kind }" :aria-pressed="activeKind === kind"
            :data-testid="`palette-filter-${kind}`" @click="selectKind(kind)"
          >
            <component :is="kindIcons[kind]" :size="18" aria-hidden="true" />
            <span>{{ t(spotlightKindLabel(kind)) }}</span><span class="search-category-count">{{ countFor(kind) }}</span>
          </button>
          <p class="search-scope-note">{{ t('palette.scopeNote') }}</p>
        </nav>

        <div class="search-results-column">
          <div class="search-results-summary" role="status" aria-live="polite">
            <span>{{ query.trim() ? t('palette.resultCount', { count: totalResults }) : t('palette.browse') }}</span>
            <span v-if="searching" class="search-progress"><LoaderCircle :size="14" aria-hidden="true" />{{ t('palette.searching') }}</span>
          </div>
          <div :id="listId" class="command-palette-list" :aria-busy="searching" data-testid="palette-list">
            <section v-for="(group, groupIndex) in visibleGroups" :key="group.kind" class="search-result-group" :data-testid="`palette-kind-${group.kind}`">
              <button
                class="search-group-toggle" :aria-expanded="!collapsedGroups.has(group.kind)"
                :aria-controls="`palette-group-${group.kind}`" :data-testid="`palette-toggle-${group.kind}`"
                @click="toggleGroup(group.kind)"
              >
                <component :is="collapsedGroups.has(group.kind) ? ChevronRight : ChevronDown" :size="14" aria-hidden="true" />
                <component :is="kindIcons[group.kind]" :size="17" aria-hidden="true" />
                <span>{{ t(spotlightKindLabel(group.kind)) }}</span>
                <span class="search-group-count">{{ group.items.length }}</span>
              </button>
              <div v-if="!collapsedGroups.has(group.kind)" :id="`palette-group-${group.kind}`">
                <div :id="`palette-results-${group.kind}`" role="listbox" :aria-label="t(spotlightKindLabel(group.kind))">
                  <div
                    v-for="(item, itemIndex) in group.visibleItems" :key="item.id"
                    :id="`command-palette-option-${rowIndex(groupIndex, itemIndex)}`"
                    class="command-palette-row" :class="{ 'is-active': rowIndex(groupIndex, itemIndex) === activeIndex }"
                    role="option" :aria-selected="rowIndex(groupIndex, itemIndex) === activeIndex"
                    :data-testid="`palette-row-${item.id}`" :title="[item.label, item.detail].filter(Boolean).join(' — ')"
                    @mouseenter="activeIndex = rowIndex(groupIndex, itemIndex)" @mousedown.prevent @click="run(item)"
                  >
                    <component :is="resultIcon(item)" :size="19" class="search-result-icon" aria-hidden="true" />
                    <span class="search-result-copy">
                      <span class="command-palette-label">{{ item.label }}</span>
                      <span v-if="item.detail" class="command-palette-detail">{{ item.detail }}</span>
                    </span>
                    <ArrowUpRight :size="15" class="search-result-arrow" aria-hidden="true" />
                  </div>
                </div>
                <p v-if="group.error" class="search-source-error" role="status" :data-testid="`palette-error-${group.kind}`">
                  <TriangleAlert :size="15" aria-hidden="true" />{{ t('palette.sourceUnavailable') }}
                </p>
                <button
                  v-if="group.items.length > PREVIEW_LIMIT" class="search-show-more"
                  :aria-expanded="expandedGroups.has(group.kind)" :data-testid="`palette-more-${group.kind}`"
                  @click="toggleMore(group.kind)"
                >
                  <component :is="expandedGroups.has(group.kind) ? ChevronRight : ChevronDown" :size="14" aria-hidden="true" />
                  {{ expandedGroups.has(group.kind) ? t('palette.showLess') : t('palette.showMore', { count: group.items.length - PREVIEW_LIMIT }) }}
                </button>
                <p v-if="group.truncated" class="search-limit-note">{{ t('palette.limitedResults') }}</p>
              </div>
            </section>
            <div v-if="!visibleGroups.length && !searching" class="command-palette-empty" role="status" data-testid="palette-empty">
              <component :is="searchFailed ? TriangleAlert : Search" :size="28" aria-hidden="true" />
              <p>{{ searchFailed ? t('palette.sourceUnavailable') : t('palette.empty') }}</p>
              <span>{{ t('palette.emptyHint') }}</span>
            </div>
            <UiButton v-if="searchFailed || groups.some(group => group.error)" variant="ghost" size="sm" class="search-retry" @click="refreshSpotlight(); runSearch()">
              <RefreshCw data-icon="inline-start" aria-hidden="true" />{{ t('common.refresh') }}
            </UiButton>
          </div>
        </div>
      </div>
      <footer class="command-palette-footer">
        <span>{{ t('palette.hintNavigate') }}</span><span>{{ t('palette.hintRun') }}</span><span>{{ t('palette.hintClose') }}</span>
      </footer>
    </div>
  </dialog>
</template>

<style scoped>
.command-palette {
  position: fixed;
  inset: auto;
  top: max(40px, 12vh);
  left: 50%;
  transform: translateX(-50%);
  width: min(760px, calc(100vw - 40px));
  height: auto;
  max-width: none;
  max-height: none;
  margin: 0 auto;
  padding: 0;
  border: 1px solid var(--border-muted);
  border-radius: 14px;
  color: var(--text-primary);
  background: var(--surface-section);
  box-shadow: none;
  -webkit-app-region: no-drag;
}
.command-palette::backdrop {
  background: color-mix(in srgb, var(--bg-primary) 16%, transparent);
  backdrop-filter: blur(6px);
  -webkit-backdrop-filter: blur(6px);
}
.search-screen { display: flex; flex-direction: column; height: min(590px, calc(100dvh - 90px)); width: 100%; margin: 0 auto; padding: 0; }
.command-palette--fullscreen { inset: 0; transform: none; width: 100%; height: 100%; margin: 0; border: 0; border-radius: 0; }
.command-palette--fullscreen .search-screen { height: 100%; width: min(1120px, 100%); padding: 44px 40px 20px; }
.search-screen-header { display: flex; align-items: flex-start; justify-content: space-between; gap: 20px; padding-bottom: 26px; }
.search-heading { flex: 1; min-width: 0; }
.search-screen-header h1 { margin: 0 0 8px; font-size: 22px; font-weight: 600; letter-spacing: -.025em; }
.search-screen-header p { margin: 0; color: var(--text-secondary); font-size: 13px; }
.search-action { flex-shrink: 0; border: 0; background: transparent; box-shadow: none; border-radius: 0; color: var(--text-secondary); }
.search-action:hover { background: transparent; color: var(--text-primary); }
.search-action:focus-visible { outline: 2px solid var(--text-brand); outline-offset: -2px; }
.command-palette-search { display: flex; align-items: center; gap: 12px; padding: 14px 18px; border-bottom: 1px solid var(--border-muted); }
.command-palette-search-icon { color: var(--text-muted); flex-shrink: 0; }
.command-palette-input { flex: 1; min-width: 0; padding: 0; border: none; background: transparent; color: var(--text-primary); font-size: 17px; outline: none; border-radius: 0; }
.command-palette-input::placeholder { color: var(--text-muted); }
.command-palette-input:focus { box-shadow: none; }
.command-palette-input::-webkit-search-cancel-button { display: none; }
.command-palette-search:focus-within { border-bottom-color: var(--text-brand); }
.command-palette-accelerator { color: var(--text-muted); font: 11px 'Geist Mono', ui-monospace, monospace; white-space: nowrap; }
.search-screen-body { flex: 1; min-height: 0; display: flex; flex-direction: column; gap: 8px; padding: 8px 18px 0; }
.search-categories { display: flex; flex-direction: row; gap: 20px; overflow-x: auto; scrollbar-width: none; padding-bottom: 5px; flex-shrink: 0; }
.search-categories::-webkit-scrollbar { display: none; }
.search-category { display: flex; align-items: center; gap: 10px; min-height: 36px; padding: 7px 12px 7px 0; text-align: left; border: 0; background: transparent; box-shadow: none; color: var(--text-secondary); font-size: 12px; cursor: pointer; flex-shrink: 0; }
.search-category svg { flex-shrink: 0; }
.search-category.is-selected { color: var(--text-brand); font-weight: 600; }
.search-category:hover { color: var(--text-primary); }
.search-category-count { margin-left: auto; color: var(--text-muted); font-size: 11px; font-variant-numeric: tabular-nums; }
.search-scope-note { display: none; }
.search-results-column { display: flex; flex-direction: column; min-height: 0; min-width: 0; flex: 1; }
.search-results-summary { display: flex; justify-content: space-between; align-items: center; gap: 16px; min-height: 30px; color: var(--text-muted); font-size: 11px; }
.search-progress { display: flex; gap: 6px; align-items: center; }
.command-palette-list { flex: 1; min-height: 0; overflow-y: auto; overscroll-behavior: contain; padding: 0 8px 12px 0; }
.search-result-group { padding: 10px 0 16px; border-bottom: 1px solid var(--border-muted); }
.search-result-group:last-child { border-bottom: 0; }
.search-group-toggle { display: flex; align-items: center; gap: 8px; width: 100%; min-height: 32px; padding: 0 2px 7px; border: 0; background: transparent; color: var(--text-secondary); font: inherit; font-size: 12px; font-weight: 600; cursor: pointer; text-align: left; }
.search-group-count { font-size: 11px; font-weight: 400; color: var(--text-muted); }
.command-palette-row { display: flex; align-items: center; gap: 12px; min-height: 50px; padding: 8px 10px; cursor: pointer; border-radius: 6px; }
.command-palette-row.is-active { background: var(--surface-hover); }
.search-result-icon { flex-shrink: 0; color: var(--text-secondary); }
.search-result-copy { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 4px; }
.command-palette-label { color: var(--text-primary); font-size: 13px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.command-palette-detail { color: var(--text-muted); font-size: 11px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.search-result-arrow { flex-shrink: 0; color: var(--text-muted); opacity: 0; }
.is-active .search-result-arrow { opacity: 1; }
.search-show-more { display: flex; align-items: center; gap: 6px; min-height: 32px; padding: 5px 12px 0 44px; border: 0; background: transparent; color: var(--text-brand); font: inherit; font-size: 11px; cursor: pointer; }
.search-source-error, .search-limit-note { display: flex; gap: 8px; align-items: center; margin: 8px 12px; font-size: 11px; line-height: 1.6; color: var(--text-muted); }
.search-source-error { color: var(--accent-warning); }
.command-palette-empty { display: flex; flex-direction: column; align-items: center; gap: 12px; padding: 68px 16px; color: var(--text-muted); text-align: center; }
.command-palette-empty p { margin: 0; color: var(--text-secondary); font-size: 14px; }
.command-palette-empty span { font-size: 12px; line-height: 1.7; }
.search-retry { margin-top: 12px; }
.command-palette-footer { display: flex; justify-content: flex-end; gap: 16px; padding: 10px 18px; border-top: 1px solid var(--border-muted); color: var(--text-muted); font-size: 10px; }
.search-category:focus-visible, .search-group-toggle:focus-visible, .search-show-more:focus-visible { outline: 2px solid var(--text-brand); outline-offset: -2px; }
@media (max-width: 720px) {
  .command-palette { top: 28px; width: calc(100vw - 24px); }
  .search-screen { height: min(590px, calc(100dvh - 56px)); }
  .command-palette--fullscreen { top: 0; width: 100%; }
  .command-palette--fullscreen .search-screen { height: 100%; padding: 40px 14px 0; }
  .search-screen-header { padding-bottom: 18px; }
  .search-screen-header h1 { font-size: 19px; }
  .command-palette-input { font-size: 17px; }
  .command-palette-accelerator { display: none; }
  .search-screen-body { gap: 8px; padding: 8px 12px 0; }
  .search-categories { flex-direction: row; overflow-x: auto; gap: 18px; padding-bottom: 8px; }
  .search-category { padding: 4px 0; gap: 6px; }
  .search-scope-note { display: none; }
  .command-palette-list { padding-right: 0; }
}
</style>
