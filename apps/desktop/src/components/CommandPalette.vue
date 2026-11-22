<script setup lang="ts">
import {
  Search, X, FolderKanban, MessageSquare, MessageSquareText, BrainCircuit, Bot, Workflow,
  FileText, FileCode2, FileJson, Image, Wrench, Settings2, Command, Paperclip, ListChecks,
  Layers3, ChevronDown, ChevronRight, ArrowUpRight, Maximize2, Minimize2, LoaderCircle,
  TriangleAlert, RefreshCw,
} from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { computed, nextTick, onBeforeUnmount, ref, watch, type Component } from 'vue'
import { useRouter } from 'vue-router'
import { UiButton } from '@/components/ui'
import AppHeader from '@/components/AppHeader.vue'
import { homeController } from '@/controllers/HomeController'
import { selectionKey } from '@/lib/storageScope'
import { focusDockCard } from '@/lib/homeDockSync'
import { closePalette, useCommandPalette } from '@/composables/useCommandPalette'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { availableCommands, implicitArgument, resolveCommandHint, resolveCommandLabel, type AppCommand, type CommandHost } from '@/lib/appCommands'
import { setActiveWorkspaceCommandsRoot, useWorkspaceCommands } from '@/composables/useWorkspaceCommands'
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
  message: MessageSquareText, artifact: Paperclip, task: ListChecks,
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

const searchProject = computed(() => homeController.currentProject.value)

watch(() => searchProject.value?.path ?? '', (path) => {
  setActiveWorkspaceCommandsRoot(path)
}, { immediate: true })

const { appCommands: workspaceAppCommands, refresh: refreshWorkspaceCommands } = useWorkspaceCommands(
  () => searchProject.value?.path ?? '',
)

const commandItems = computed<SpotlightItem[]>(() => availableCommands(commandHost, workspaceAppCommands.value).map((command) => {
  const hint = resolveCommandHint(command, (key) => t(key))
  return {
    id: command.id,
    kind: 'command',
    label: resolveCommandLabel(command, (key) => t(key)),
    detail: command.slash ? (hint ? `/${command.slash} · ${hint}` : `/${command.slash}`) : hint,
    keywords: [...(command.keywordKeys ?? []).map((key) => t(key)), ...(command.keywords ?? [])].join(' '),
    action: () => runCommand(command),
  }
}))

function navigate(routeName: string) {
  if (router.currentRoute.value.name !== routeName) void router.push({ name: routeName })
}
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
    navigate('home')
    focusDockCard({ card: 'workspace', filePath: path })
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
  void refreshWorkspaceCommands()
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

function splitHintKeycap(raw: string): { key: string | null; label: string } {
  const match = /^(\S+)\s+([\s\S]+)$/.exec(raw.trim())
  if (!match) return { key: null, label: raw }
  return { key: match[1]!, label: match[2]!.replace(/^—\s*/, '') }
}

const footerHints = computed(() => [
  splitHintKeycap(t('palette.hintNavigate')),
  splitHintKeycap(t('palette.hintRun')),
  splitHintKeycap(t('palette.hintClose')),
])
</script>

<template>
  <dialog
    ref="dialogRef"
    class="command-palette ui-pop no-drag"
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
        <Search class="command-palette-search-icon" :size="20" aria-hidden="true" />
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
        <kbd class="command-palette-accelerator command-palette-kbd">{{ comboLabel }}</kbd>
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
            <Layers3 :size="16" aria-hidden="true" /><span>{{ t('palette.all') }}</span>
            <span class="search-category-count">{{ groups.reduce((count, group) => count + group.items.length, 0) }}</span>
          </button>
          <button
            v-for="kind in spotlightKindOrder" :key="kind" class="search-category"
            :class="{ 'is-selected': activeKind === kind }" :aria-pressed="activeKind === kind"
            :data-testid="`palette-filter-${kind}`" @click="selectKind(kind)"
          >
            <component :is="kindIcons[kind]" :size="16" aria-hidden="true" />
            <span>{{ t(spotlightKindLabel(kind)) }}</span><span class="search-category-count">{{ countFor(kind) }}</span>
          </button>
          <p class="search-scope-note">{{ t('palette.scopeNote') }}</p>
        </nav>

        <div class="search-results-column">
          <div class="search-results-summary" role="status" aria-live="polite">
            <span>{{ query.trim() ? t('palette.resultCount', { count: totalResults }) : t('palette.browse') }}</span>
            <span v-if="searching" class="search-progress"><LoaderCircle :size="13" class="search-progress-icon" aria-hidden="true" /><span class="ui-shimmer-text">{{ t('palette.searching') }}</span></span>
          </div>
          <div :id="listId" class="command-palette-list" :aria-busy="searching" data-testid="palette-list">
            <section v-for="(group, groupIndex) in visibleGroups" :key="group.kind" class="search-result-group" :data-testid="`palette-kind-${group.kind}`">
              <button
                class="search-group-toggle" :aria-expanded="!collapsedGroups.has(group.kind)"
                :aria-controls="`palette-group-${group.kind}`" :data-testid="`palette-toggle-${group.kind}`"
                @click="toggleGroup(group.kind)"
              >
                <component :is="collapsedGroups.has(group.kind) ? ChevronRight : ChevronDown" :size="14" aria-hidden="true" />
                <component :is="kindIcons[group.kind]" :size="16" aria-hidden="true" />
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
                    <component :is="resultIcon(item)" :size="18" class="search-result-icon" aria-hidden="true" />
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
        <span v-for="(hint, idx) in footerHints" :key="idx" class="command-palette-hint">
          <kbd v-if="hint.key" class="command-palette-kbd">{{ hint.key }}</kbd>
          <span>{{ hint.label }}</span>
        </span>
      </footer>
    </div>
  </dialog>
</template>

<style scoped>
/* Centered without translateX(-50%) so dialog.ui-pop[open] settles to transform: none. */
.command-palette {
  position: fixed;
  inset: max(40px, 12vh) 0 auto 0;
  width: min(760px, calc(100vw - 40px));
  height: auto;
  max-width: none;
  max-height: none;
  margin: 0 auto;
  padding: 0;
  border: 1px solid var(--border-default, var(--border-muted));
  border-radius: 14px;
  color: var(--text-primary);
  background: var(--surface-section);
  box-shadow: var(--shadow-elevated, 0 16px 48px rgba(0, 0, 0, 0.28));
  -webkit-app-region: no-drag;
}
.command-palette::backdrop {
  background: color-mix(in srgb, var(--bg-primary) 26%, transparent);
  backdrop-filter: blur(6px);
  -webkit-backdrop-filter: blur(6px);
}
.search-screen { display: flex; flex-direction: column; height: min(590px, calc(100dvh - 90px)); width: 100%; margin: 0 auto; padding: 0; }
.command-palette--fullscreen,
.command-palette--fullscreen[open] { inset: 0; transform: none; width: 100%; height: 100%; margin: 0; border: 0; border-radius: 0; box-shadow: none; }
.command-palette--fullscreen .search-screen { height: 100%; width: min(1120px, 100%); padding: 44px 40px 20px; }
.search-screen-header { display: flex; align-items: flex-start; justify-content: space-between; gap: 20px; padding-bottom: 26px; }
.search-heading { flex: 1; min-width: 0; }
.search-screen-header h1 { margin: 0 0 8px; font-size: 22px; font-weight: 600; letter-spacing: -.025em; }
.search-screen-header p { margin: 0; color: var(--text-secondary); font-size: 13px; }
.search-action { flex-shrink: 0; border: 0; background: transparent; box-shadow: none; border-radius: 6px; color: var(--text-secondary); transition: background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)), color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)); }
.search-action:hover { background: var(--surface-hover); color: var(--text-primary); }
.search-action:focus-visible { outline: 2px solid var(--accent-primary, var(--text-brand)); outline-offset: -2px; }
.command-palette-search { display: flex; align-items: center; gap: 10px; padding: 14px 18px; border-bottom: 1px solid var(--border-muted); transition: border-color var(--duration-pop, 180ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)); }
.command-palette-search-icon { color: var(--text-muted); flex-shrink: 0; transition: color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)); }
.command-palette-search:focus-within .command-palette-search-icon { color: var(--text-primary); }
.command-palette-input { flex: 1; min-width: 0; padding: 0; border: none; background: transparent; color: var(--text-primary); font-size: 16px; outline: none; border-radius: 0; }
.command-palette-input::placeholder { color: var(--text-muted); }
.command-palette-input:focus { box-shadow: none; }
.command-palette-input::-webkit-search-cancel-button { display: none; }
.command-palette-search:focus-within { border-bottom-color: var(--border-input-focus, var(--border-default)); }
/* Crisp monochrome <kbd> keycaps: 1px border + delicate inset bottom edge */
.command-palette-kbd,
.command-palette-accelerator {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-height: 20px;
  padding: 1px 6px;
  border-radius: 5px;
  border: 1px solid var(--border-default, var(--border-muted));
  background: var(--surface-raised);
  box-shadow: inset 0 -1px 0 var(--border-muted);
  color: var(--text-secondary);
  font-family: var(--font-mono, 'Geist Mono', ui-monospace, monospace);
  font-size: 11px;
  font-weight: 500;
  font-variant-numeric: tabular-nums;
  line-height: 1.2;
  white-space: nowrap;
}
.search-screen-body { flex: 1; min-height: 0; display: flex; flex-direction: column; gap: 8px; padding: 10px 18px 0; }
.search-categories { display: flex; flex-direction: row; gap: 6px; overflow-x: auto; scrollbar-width: none; padding-bottom: 4px; flex-shrink: 0; }
.search-categories::-webkit-scrollbar { display: none; }
.search-category { display: inline-flex; align-items: center; gap: 7px; min-height: 30px; padding: 4px 10px; text-align: left; border: 1px solid transparent; border-radius: 999px; background: transparent; box-shadow: none; color: var(--text-secondary); font-size: 12px; cursor: pointer; flex-shrink: 0; transition: background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)), border-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)), color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)); }
.search-category svg { flex-shrink: 0; }
.search-category:hover { background: var(--surface-hover); color: var(--text-primary); }
.search-category.is-selected { background: var(--surface-raised); border-color: var(--border-default, var(--border-muted)); color: var(--text-primary); font-weight: 500; }
.search-category-count { margin-left: 2px; color: var(--text-muted); font-size: 11px; font-variant-numeric: tabular-nums; }
.search-scope-note { display: none; }
.search-results-column { display: flex; flex-direction: column; min-height: 0; min-width: 0; flex: 1; }
.search-results-summary { display: flex; justify-content: space-between; align-items: center; gap: 16px; min-height: 28px; color: var(--text-muted); font-size: 11px; font-variant-numeric: tabular-nums; }
.search-progress { display: inline-flex; gap: 6px; align-items: center; }
.search-progress-icon { animation: palette-spin 1s linear infinite; color: var(--text-muted); }
@keyframes palette-spin { to { transform: rotate(360deg); } }
.command-palette-list { flex: 1; min-height: 0; overflow-y: auto; overscroll-behavior: contain; padding: 0 4px 12px 0; }
.search-result-group { padding: 8px 0 12px; border-bottom: 1px solid var(--border-muted); }
.search-result-group:last-child { border-bottom: 0; }
.search-group-toggle { display: flex; align-items: center; gap: 7px; width: 100%; min-height: 30px; padding: 0 4px 6px; border: 0; background: transparent; color: var(--text-secondary); font: inherit; font-size: 11.5px; font-weight: 600; cursor: pointer; text-align: left; transition: color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)); }
.search-group-toggle:hover { color: var(--text-primary); }
.search-group-count { font-size: 11px; font-weight: 400; color: var(--text-muted); font-variant-numeric: tabular-nums; }
.command-palette-row {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 46px;
  padding: 7px 10px;
  cursor: pointer;
  border-radius: 8px;
  border: 1px solid transparent;
  transition:
    background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    border-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}
.command-palette-row.is-active {
  background: var(--surface-hover);
  border-color: var(--border-card-active, var(--border-muted));
}
.search-result-icon {
  flex-shrink: 0;
  color: var(--text-muted);
  transition: color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}
.command-palette-row.is-active .search-result-icon {
  color: var(--text-primary);
}
.search-result-copy { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
.command-palette-label { color: var(--text-primary); font-size: 13px; font-weight: 450; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.command-palette-detail { color: var(--text-muted); font-size: 11px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.search-result-arrow {
  flex-shrink: 0;
  color: var(--text-secondary);
  opacity: 0;
  transform: translate(-2px, 2px);
  transition:
    opacity var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    transform var(--duration-tap, 120ms) var(--ease-spring-soft, cubic-bezier(0.16, 1, 0.3, 1));
}
.is-active .search-result-arrow {
  opacity: 1;
  transform: translate(0, 0);
}
.search-show-more { display: flex; align-items: center; gap: 6px; min-height: 30px; padding: 4px 12px 0 42px; border: 0; background: transparent; color: var(--text-secondary); font: inherit; font-size: 11px; cursor: pointer; transition: color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)); }
.search-show-more:hover { color: var(--text-primary); }
.search-source-error, .search-limit-note { display: flex; gap: 8px; align-items: center; margin: 8px 12px; font-size: 11px; line-height: 1.6; color: var(--text-muted); }
.search-source-error { color: var(--accent-warning); }
.command-palette-empty { display: flex; flex-direction: column; align-items: center; gap: 12px; padding: 68px 16px; color: var(--text-muted); text-align: center; }
.command-palette-empty p { margin: 0; color: var(--text-secondary); font-size: 14px; }
.command-palette-empty span { font-size: 12px; line-height: 1.7; }
.search-retry { margin-top: 12px; }
.command-palette-footer { display: flex; justify-content: flex-end; align-items: center; gap: 16px; padding: 9px 18px; border-top: 1px solid var(--border-muted); color: var(--text-muted); font-size: 11px; }
.command-palette-hint { display: inline-flex; align-items: center; gap: 5px; }
.command-palette-footer .command-palette-kbd { min-height: 18px; padding: 0 5px; font-size: 10px; }
.search-category:focus-visible, .search-group-toggle:focus-visible, .search-show-more:focus-visible { outline: 2px solid var(--accent-primary, var(--text-brand)); outline-offset: -2px; }
@media (prefers-reduced-motion: reduce) {
  .search-action,
  .command-palette-search,
  .command-palette-search-icon,
  .search-category,
  .search-group-toggle,
  .command-palette-row,
  .search-result-icon,
  .search-result-arrow,
  .search-show-more {
    transition: none;
  }
  .search-progress-icon {
    animation: none;
  }
  .search-result-arrow {
    transform: none;
  }
}
@media (max-width: 720px) {
  .command-palette { top: 28px; width: calc(100vw - 24px); }
  .search-screen { height: min(590px, calc(100dvh - 56px)); }
  .command-palette--fullscreen,
  .command-palette--fullscreen[open] { top: 0; width: 100%; }
  .command-palette--fullscreen .search-screen { height: 100%; padding: 40px 14px 0; }
  .search-screen-header { padding-bottom: 18px; }
  .search-screen-header h1 { font-size: 19px; }
  .command-palette-input { font-size: 16px; }
  .command-palette-accelerator { display: none; }
  .search-screen-body { gap: 8px; padding: 8px 12px 0; }
  .search-categories { flex-direction: row; overflow-x: auto; gap: 6px; padding-bottom: 6px; }
  .search-category { padding: 4px 8px; gap: 6px; }
  .search-scope-note { display: none; }
  .command-palette-list { padding-right: 0; }
}
</style>
