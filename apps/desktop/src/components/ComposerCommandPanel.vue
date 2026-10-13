<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch, type Component } from 'vue'
import { ArrowLeft, Check, ChevronRight, FileText, GitBranch, Image, Layers, ListChecks, MessageSquare, Network, Search, Settings, Sparkles, Users, Workflow, X } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { api, type AgentModeTopologyDto, type SpaceOptionsDto } from '@/api'
import type { PermissionLevel } from '@/types/mode'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { isCommandAvailable, slashCommands, type AppCommand, type CommandHost } from '@/lib/appCommands'
import { composerSettings, permissionChoices, type ComposerPage, type PermissionRisk } from '@/lib/composerCommands'
import { defaultSpaceOptions } from '@/lib/composerSettings'
import { modeIcon, sortModes } from '@/lib/modePresentation'
import type { DefaultModel } from '@/composables/useDefaultModel'

const props = defineProps<{
  open: boolean
  anchor: HTMLElement | null
  spatial?: boolean
  slashQuery?: string | null
  initialPage?: ComposerPage
  canAttach: boolean
  permission: PermissionLevel
  modeVersionId?: string | null
  spaceOptions?: SpaceOptionsDto | null
  /** The default model (chat route); its page switches it for every conversation. */
  defaultModel?: DefaultModel
  defaultModelLabel?: string
  settingsSaving?: boolean
  settingsError?: string | null
  commandHost: CommandHost
}>()
const emit = defineEmits<{
  close: []
  'focus-composer': []
  attach: [accept: string]
  action: [command: AppCommand]
  'send-as-text': []
  'update:modeVersionId': [value: string | null]
  'update:permission': [value: PermissionLevel]
  'update:spaceOptions': [value: SpaceOptionsDto]
  'manage-models': []
  'mode-label': [value: string, unavailable: boolean, icon: Component]
}>()

const { t } = useI18n()
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
const panelStyle = computed(() => getPanelStyle())
const panelDataAttrs = computed(() => getPanelDataAttributes())
const panelRef = ref<HTMLElement | null>(null)
const contentRef = ref<HTMLElement | null>(null)
const searchRef = ref<HTMLInputElement | null>(null)
const position = ref<Record<string, string>>({ position: 'fixed' })
const page = ref<ComposerPage>('root')
const query = ref('')
const rootQuery = ref('')
const index = ref(0)
const modes = ref<AgentModeTopologyDto[]>([])
const loading = ref(false)
const modeError = ref(false)
const space = computed(() => props.spaceOptions ?? defaultSpaceOptions())
let loadSequence = 0
let anchorObserver: ResizeObserver | null = null
let navigationDeleteKey: string | null = null
let pageAnimations: Animation[] = []

function cancelPageAnimations() {
  pageAnimations.forEach(animation => animation.cancel())
  pageAnimations = []
}
function animateContent(offset: number, previousHeight?: number) {
  const panel = panelRef.value
  const content = contentRef.value
  if (!panel || !content || typeof content.animate !== 'function'
      || window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) return
  const style = getComputedStyle(panel)
  const options = {
    duration: parseFloat(style.getPropertyValue('--command-panel-transition-duration')) || 150,
    easing: style.getPropertyValue('--command-panel-transition-easing').trim() || 'cubic-bezier(0.2, 0, 0, 1)',
  }
  const height = panel.getBoundingClientRect().height
  if (previousHeight && Math.abs(height - previousHeight) > 1 && typeof panel.animate === 'function') {
    const animation = panel.animate([{ height: `${previousHeight}px` }, { height: `${height}px` }], options)
    animation.finished?.catch(() => {})
    pageAnimations.push(animation)
  }
  const animation = content.animate([{ opacity: 0, transform: `translateX(${offset}px)` }, { opacity: 1, transform: 'translateX(0)' }], options)
  animation.finished?.catch(() => {})
  pageAnimations.push(animation)
}
watch(page, async (next, previous) => {
  const previousHeight = panelRef.value?.getBoundingClientRect().height
  cancelPageAnimations()
  await nextTick()
  if (props.open && page.value === next && next !== previous) animateContent(next === 'root' ? -8 : 8, previousHeight)
})

const availableModes = computed(() => {
  const seen = new Set<string>()
  return sortModes(modes.value.filter(mode => {
    if (mode.status !== 'published' || !mode.latest_published_mode_version_id) return false
    const key = mode.display_name.trim().toLowerCase()
    if (seen.has(key)) return false
    seen.add(key)
    return true
  }))
})
const selectedMode = computed(() => availableModes.value.find(mode => mode.latest_published_mode_version_id === props.modeVersionId))
watch([selectedMode, () => props.modeVersionId, loading], () => {
  emit('mode-label', selectedMode.value?.display_name ?? t('chat.followDefault'), !!props.modeVersionId && !selectedMode.value && !loading.value, selectedMode.value ? modeIcon(selectedMode.value.slug) : Layers)
}, { immediate: true })
const workflowName = computed(() => availableModes.value.find(mode => mode.latest_published_mode_version_id === space.value.workflow_mode_version_id)?.display_name)
const currentPermission = computed(() => permissionChoices.find(choice => choice.value === props.permission)!)
const permissionName = computed(() => t(currentPermission.value.label))
const title = computed(() => page.value === 'root' ? t('composer.commands') : t(`commandPanel.${page.value}`))
const activeQuery = computed(() => page.value === 'root' && props.slashQuery != null ? props.slashQuery : query.value)
const catalogError = computed(() => ['mode', 'workflow'].includes(page.value) ? modeError.value : false)

interface Row {
  id: string
  label: string
  hint?: string
  keywords?: string
  value?: string
  icon?: Component
  risk?: PermissionRisk
  checked?: boolean
  switch?: boolean
  submenu?: boolean
  disabled?: boolean
  testId?: string
  run: () => void
}
const icons: Record<string, Component> = { model: Sparkles, mode: Layers, plan: ListChecks, spec: FileText, agents: Users, workflow: Workflow, bulletin: MessageSquare, worktree: GitBranch }
function selectPage(next: ComposerPage) {
  if (next === page.value) return
  rootQuery.value = query.value
  query.value = ''
  page.value = next
  index.value = 0
  void nextTick(() => searchRef.value?.focus())
}
function back() {
  page.value = 'root'
  query.value = rootQuery.value
  index.value = 0
  void nextTick(() => {
    if (props.slashQuery != null) emit('focus-composer')
    else searchRef.value?.focus()
  })
}
function modeRows(workflow: boolean): Row[] {
  const selected = workflow ? space.value.workflow_mode_version_id : props.modeVersionId
  return [{
    id: 'default', label: workflow ? t('commandPanel.workflowOff') : t('chat.followDefault'),
    icon: workflow ? Workflow : Layers, checked: !selected, disabled: props.settingsSaving,
    run: () => {
      if (workflow) emit('update:spaceOptions', { ...space.value, workflow_mode_version_id: null })
      else if (!workflow) emit('update:modeVersionId', null)
    },
  }, ...availableModes.value.filter(mode => !workflow || mode.edges.length > 0).map(mode => ({
    id: mode.latest_published_mode_version_id!, label: mode.display_name, hint: mode.description ?? '',
    icon: modeIcon(mode.slug),
    keywords: mode.slug ?? '', checked: selected === mode.latest_published_mode_version_id,
    disabled: props.settingsSaving,
    run: () => {
      if (workflow) emit('update:spaceOptions', { ...space.value, workflow_mode_version_id: mode.latest_published_mode_version_id! })
      else if (!workflow) emit('update:modeVersionId', mode.latest_published_mode_version_id!)
    },
  }))]
}
const allRows = computed<Row[]>(() => {
  if (page.value === 'permission') return permissionChoices.map(choice => ({
    id: choice.value, label: t(choice.label), hint: t(choice.hint), icon: choice.icon, risk: choice.risk, checked: props.permission === choice.value,
    disabled: props.settingsSaving, run: () => emit('update:permission', choice.value),
  }))
  if (page.value === 'mode' || page.value === 'workflow') return modeRows(page.value === 'workflow')
  if (page.value === 'model') {
    const model = props.defaultModel
    const choices: Row[] = (model?.models.value ?? []).map(row => ({
      id: `${row.provider_instance_id}:${row.model_id}`, label: row.display_name || row.model_id,
      hint: row.provider_display_name ?? undefined, keywords: `${row.model_id} ${row.provider_display_name ?? ''}`, icon: Sparkles,
      checked: model?.isCurrent(row.provider_instance_id, row.model_id) ?? false, disabled: model?.busy.value ?? false,
      run: () => { void model?.choose(row.provider_instance_id, row.model_id) },
    }))
    if (model?.loaded.value && !choices.length)
      choices.push({ id: 'no-models', label: t('composer.modelSwitch.empty'), icon: Sparkles, disabled: true, run: () => {} })
    return [...choices, { id: 'manage-models', label: t('composer.modelSwitch.manage'), icon: Settings, run: () => emit('manage-models') }]
  }
  const rows: Row[] = [
    { id: 'image', label: t('chat.addImage'), hint: props.canAttach ? undefined : t('commandPanel.attachmentsBusy'), keywords: 'image picture 图片', icon: Image, disabled: !props.canAttach, testId: 'composer-attach-image', run: () => emit('attach', 'image/*') },
    { id: 'file', label: t('chat.addFile'), hint: props.canAttach ? undefined : t('commandPanel.attachmentsBusy'), keywords: 'file attachment 文件 附件', icon: FileText, disabled: !props.canAttach, testId: 'composer-attach-file', run: () => emit('attach', '') },
  ]
  for (const setting of composerSettings) {
    if ('surface' in setting && (setting.surface === 'space') !== Boolean(props.spatial)) continue
    const toggle = 'toggle' in setting ? setting.toggle : null
    const submenu = 'page' in setting ? setting.page : null
    const currentValue = setting.id === 'model' ? props.defaultModelLabel : setting.id === 'mode' ? selectedMode.value?.display_name ?? t('chat.followDefault') : setting.id === 'permission' ? permissionName.value : setting.id === 'workflow' ? workflowName.value ?? (space.value.workflow_mode_version_id ? t('commandPanel.unavailableSelection') : t('commandPanel.off')) : undefined
    rows.push({
      id: setting.id, label: t(setting.label), hint: t(setting.hint),
      keywords: setting.keywords,
      icon: setting.id === 'permission' ? currentPermission.value.icon : setting.id === 'mode' && selectedMode.value ? modeIcon(selectedMode.value.slug) : icons[setting.id],
      risk: setting.id === 'permission' ? currentPermission.value.risk : undefined, value: currentValue,
      switch: !!toggle, checked: toggle ? Boolean(space.value[toggle]) : undefined, submenu: !!submenu,
      disabled: props.settingsSaving,
      run: () => {
        if (toggle) emit('update:spaceOptions', { ...space.value, [toggle]: !space.value[toggle] })
        else if (submenu) selectPage(submenu)
      },
    })
  }
  for (const command of slashCommands().filter(command => isCommandAvailable(command, props.commandHost))) {
    rows.push({ id: command.id, label: t(command.labelKey), hint: command.hintKey ? t(command.hintKey) : undefined,
      keywords: `${command.slash} ${(command.keywordKeys ?? []).map(key => t(key)).join(' ')}`, value: `/${command.slash}`, icon: Network,
      testId: `composer-command-${command.id}`, run: () => emit('action', command) })
  }
  return rows
})
const rows = computed(() => {
  const search = activeQuery.value.trim().replace(/^\/+/, '').toLowerCase()
  return allRows.value
    .filter(row => !search || `${row.label} ${row.hint ?? ''} ${row.keywords ?? ''} ${row.value ?? ''}`.toLowerCase().includes(search))
    .sort((a, b) => {
      if (!search) return 0
      const exact = (row: Row) => row.id === search || row.value === `/${search}` ? 0 : 1
      return exact(a) - exact(b)
    })
})
watch(rows, () => { index.value = Math.max(0, rows.value.findIndex(row => !row.disabled)) })

function activate(row = rows.value[index.value]) {
  if (!row || row.disabled) return
  row.run()
  if (!row.submenu) emit('close')
}
function keydown(event: KeyboardEvent): boolean {
  if (!props.open || event.isComposing || event.keyCode === 229) return false
  const deleting = event.key === 'Backspace' || event.key === 'Delete'
  if (deleting && !event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey) {
    if (event.repeat && navigationDeleteKey === event.key) {
      event.preventDefault()
      return true
    }
    const target = event.target
    const editing = target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement
      || target instanceof HTMLElement && target.isContentEditable
    if (!event.repeat && page.value !== 'root' && (!editing || target === searchRef.value && searchRef.value?.value === '')) {
      event.preventDefault()
      event.stopPropagation()
      navigationDeleteKey = event.key
      back()
      return true
    }
  }
  if (event.key === 'Escape') {
    event.preventDefault()
    event.stopPropagation()
    if (page.value !== 'root') back()
    else emit('close')
    return true
  }
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault()
    const candidates = rows.value.map((row, i) => row.disabled ? -1 : i).filter(i => i >= 0)
    if (!candidates.length) return true
    const current = candidates.indexOf(index.value)
    index.value = candidates[(current + (event.key === 'ArrowDown' ? 1 : -1) + candidates.length) % candidates.length]!
    void nextTick(() => {
      const row = panelRef.value?.querySelector<HTMLElement>(`[data-row-index="${index.value}"]`)
      row?.scrollIntoView?.({ block: 'nearest' })
      if (event.target instanceof HTMLButtonElement) row?.focus()
    })
    return true
  }
  if ((event.key === 'Enter' && !event.shiftKey) || event.key === 'Tab' && props.slashQuery != null && event.target !== searchRef.value) {
    // Confirm the focused option through the same path as search selection.
    // Header/retry buttons retain their own native keyboard activation.
    const button = event.target instanceof HTMLButtonElement ? event.target : null
    if (button && !button.hasAttribute('data-row-index')) return false
    event.preventDefault()
    activate(button ? rows.value[Number(button.dataset.rowIndex)] : undefined)
    return true
  }
  return false
}
function place() {
  if (!props.open || !props.anchor) return
  const rect = props.anchor.getBoundingClientRect()
  const viewport = window.visualViewport
  const leftEdge = viewport?.offsetLeft ?? 0
  const topEdge = viewport?.offsetTop ?? 0
  const viewportWidth = viewport?.width ?? window.innerWidth
  const viewportHeight = viewport?.height ?? window.innerHeight
  const width = Math.min(460, rect.width || 460, viewportWidth - 16)
  const availableAbove = rect.top - topEdge - 16
  // Prefer above the complete composer. A small viewport can put the welcome box
  // too close to the top; keep the panel usable in the viewport in that case.
  const height = Math.max(120, Math.min(460, viewportHeight - 24, availableAbove >= 180 ? availableAbove : viewportHeight - 24))
  position.value = {
    position: 'fixed', width: `${width}px`, maxHeight: `${height}px`,
    left: `${Math.max(leftEdge + 8, Math.min(rect.left, leftEdge + viewportWidth - width - 8))}px`,
    bottom: `${Math.max(8, window.innerHeight - Math.max(topEdge + height + 16, rect.top) + 8)}px`,
  }
}
async function loadCatalogs() {
  const sequence = ++loadSequence
  loading.value = true
  void props.defaultModel?.load()
  const results = await Promise.allSettled([api.listAgentModeTopologies()])
  if (sequence !== loadSequence) return
  const [modeResult] = results
  modeError.value = modeResult.status === 'rejected'
  if (modeResult.status === 'fulfilled') modes.value = Array.isArray(modeResult.value) ? modeResult.value : []
  loading.value = false
}
watch(() => props.open, async open => {
  cancelPageAnimations()
  if (!open) { navigationDeleteKey = null; return }
  page.value = props.initialPage ?? 'root'
  query.value = ''
  rootQuery.value = ''
  index.value = 0
  void loadCatalogs()
  await nextTick()
  place()
  animateContent(0)
  if (props.slashQuery == null) searchRef.value?.focus()
}, { immediate: true })
watch(() => props.initialPage, next => { if (props.open && next) selectPage(next) })
watch(() => props.anchor, (anchor, previous) => {
  if (previous) anchorObserver?.unobserve(previous)
  if (anchor) anchorObserver?.observe(anchor)
  place()
})
watch(() => props.slashQuery, (next, previous) => {
  if (props.open && next != null && next !== previous) {
    page.value = 'root'
    index.value = 0
  }
})
function outside(event: Event) {
  if (!props.open) return
  const target = event.target as Node
  if (panelRef.value?.contains(target)) return
  const trigger = target instanceof Element ? target.closest('[aria-haspopup="dialog"]') : null
  if (trigger && props.anchor?.contains(trigger)) return
  emit('close')
}
function releaseNavigationKey(event: KeyboardEvent) {
  if (event.key === navigationDeleteKey) navigationDeleteKey = null
}
onMounted(() => {
  if (!props.open) void loadCatalogs()
  if (typeof ResizeObserver !== 'undefined') {
    anchorObserver = new ResizeObserver(place)
    if (props.anchor) anchorObserver.observe(props.anchor)
  }
  document.addEventListener('pointerdown', outside, true)
  document.addEventListener('click', outside, true)
  document.addEventListener('keyup', releaseNavigationKey, true)
  window.addEventListener('resize', place)
  window.addEventListener('scroll', place, true)
  window.visualViewport?.addEventListener('resize', place)
  window.visualViewport?.addEventListener('scroll', place)
})
onUnmounted(() => {
  loadSequence++
  anchorObserver?.disconnect()
  cancelPageAnimations()
  document.removeEventListener('pointerdown', outside, true)
  document.removeEventListener('click', outside, true)
  document.removeEventListener('keyup', releaseNavigationKey, true)
  window.removeEventListener('resize', place)
  window.removeEventListener('scroll', place, true)
  window.visualViewport?.removeEventListener('resize', place)
  window.visualViewport?.removeEventListener('scroll', place)
})
defineExpose({ keydown, activate, page, selectPage })
</script>

<template>
  <Teleport to="body">
    <section v-if="open" ref="panelRef" class="composer-command-panel" data-testid="composer-commands" role="dialog" :aria-label="title" :aria-busy="settingsSaving || loading" :style="[position, panelStyle]" v-bind="panelDataAttrs" @keydown="keydown">
      <div ref="contentRef" class="command-panel-content" :data-page="page">
      <header class="command-panel-header">
        <button v-if="page !== 'root'" class="command-panel-icon command-panel-back" :aria-label="t('commandPanel.back')" :title="t('commandPanel.backHint')" @click="back"><ArrowLeft :size="16" /><span>{{ t('commandPanel.back') }}</span></button>
        <strong>{{ title }}</strong>
        <button class="command-panel-icon command-panel-close" :aria-label="t('commandPanel.close')" @click="emit('close')"><X :size="15" /></button>
      </header>
      <label class="command-panel-search">
        <Search :size="15" aria-hidden="true" />
        <input ref="searchRef" :value="activeQuery" :readonly="page === 'root' && slashQuery != null" :placeholder="t('commandPanel.search')" :aria-label="t('commandPanel.search')" @input="query = ($event.target as HTMLInputElement).value" />
      </label>
      <div class="command-panel-list">
        <p v-if="catalogError" class="command-panel-feedback" role="alert">{{ t('commandPanel.catalogError') }} <button @click="loadCatalogs">{{ t('commandPanel.retry') }}</button></p>
        <p v-if="loading && page !== 'root'" class="command-panel-note" role="status">{{ t('commandPanel.loading') }}</p>
        <button v-for="(row, rowIndex) in rows" :key="row.id" class="command-panel-row" :class="{ 'is-highlighted': rowIndex === index, 'is-selected': row.checked, 'mode-selector-item': page === 'mode', active: page === 'mode' && row.checked }" :role="row.switch ? 'switch' : undefined" :aria-checked="row.switch ? row.checked : undefined" :aria-pressed="!row.switch && row.checked !== undefined ? row.checked : undefined" :aria-label="row.risk && row.risk !== 'neutral' ? `${row.label} · ${t(`commandPanel.risk.${row.risk}`)}` : undefined" :disabled="row.disabled" :data-testid="row.testId ?? `command-panel-${row.id}`" :data-row-index="rowIndex" :title="row.hint" @mouseenter="index = rowIndex" @focus="index = rowIndex" @click="activate(row)">
          <component :is="row.icon" v-if="row.icon" :size="16" class="command-panel-row-icon" :data-risk="row.risk" aria-hidden="true" />
          <span class="command-panel-copy"><strong>{{ row.label }}</strong><small v-if="row.hint">{{ row.hint }}</small></span>
          <span v-if="row.switch" class="command-panel-switch" :class="{ 'is-on': row.checked }" aria-hidden="true"><span /></span>
          <template v-else><span v-if="row.value" class="command-panel-value">{{ row.value }}</span><ChevronRight v-if="row.submenu" :size="14" aria-hidden="true" /><Check v-else-if="row.checked" :size="16" aria-hidden="true" /></template>
        </button>
        <p v-if="!rows.length" class="command-panel-note">{{ t('commandPanel.empty') }}</p>
        <button v-if="page === 'root' && slashQuery && !rows.length" class="command-panel-row" data-testid="command-panel-send-as-text" @click="emit('send-as-text')"><FileText :size="16" class="command-panel-row-icon" aria-hidden="true" />{{ t('commandPanel.sendAsText') }}</button>
        <p v-if="page === 'workflow'" class="command-panel-note">{{ t('commandPanel.workflowRequirement') }}</p>
      </div>
      </div>
      <footer class="command-panel-footer" :class="{ 'has-error': settingsError }" :role="settingsError ? 'alert' : 'status'">{{ settingsError || (settingsSaving ? t('commandPanel.saving') : t(page === 'model' ? 'commandPanel.modelFooter' : 'commandPanel.nextTask')) }}</footer>
    </section>
  </Teleport>
</template>

<style scoped>
.composer-command-panel { --command-panel-transition-duration: var(--durationFast, 150ms); --command-panel-transition-easing: var(--curveDecelerateMid, cubic-bezier(0.2, 0, 0, 1)); z-index: 9999; display: flex; flex-direction: column; overflow: hidden; border: 1px solid var(--border-muted); border-radius: 16px; background: var(--surface-section); box-shadow: var(--shadow-panel); color: var(--text-primary); font-size: 12px; }
.command-panel-content { display: flex; flex-direction: column; min-height: 0; flex: 1 1 auto; }
.command-panel-header { display: flex; align-items: center; gap: 8px; padding: 10px 12px 2px; flex-shrink: 0; }
.command-panel-header strong { font-weight: 600; }
.command-panel-icon { width: 28px; height: 28px; display: grid; place-items: center; background: transparent; border: 0; border-radius: 7px; color: var(--text-secondary); cursor: pointer; }
.command-panel-icon:hover { background: var(--surface-hover); }
.command-panel-back { width: auto; display: inline-flex; gap: 4px; padding: 0 6px; }
.command-panel-close { margin-left: auto; }
.command-panel-search { display: flex; gap: 8px; align-items: center; color: var(--text-muted); margin: 4px 12px 8px; padding: 8px; border-radius: 8px; background: var(--surface-input); flex-shrink: 0; font-weight: 400; }
.command-panel-search input { min-width: 0; width: 100%; height: auto; min-height: 0; padding: 0; border-radius: 0; line-height: 1.5; color: var(--text-primary); background: transparent; border: 0; box-shadow: none; outline: none; font: inherit; }
.command-panel-search:focus-within { outline: 1px solid var(--border-strong, var(--text-muted)); }
.command-panel-list { overflow-y: auto; overscroll-behavior: contain; min-height: 0; padding: 0 6px 6px; }
.command-panel-row { width: 100%; display: flex; align-items: center; gap: 8px; text-align: left; padding: 6px 10px; min-height: 36px; background: transparent; border: 0; border-radius: 9px; color: inherit; font: inherit; cursor: pointer; }
.command-panel-row.is-highlighted:not(:disabled), .command-panel-row:hover:not(:disabled) { background: var(--surface-hover); }
.command-panel-row:disabled { opacity: .5; cursor: not-allowed; }
.command-panel-row:focus-visible, .command-panel-icon:focus-visible { outline: 2px solid var(--text-muted); outline-offset: -2px; }
.command-panel-row-icon { flex-shrink: 0; color: var(--text-secondary); }
.command-panel-row-icon[data-risk='low'] { color: color-mix(in srgb, var(--accent-info) 80%, var(--text-primary)); }
.command-panel-row-icon[data-risk='medium'] { color: var(--accent-warning); }
.command-panel-row-icon[data-risk='high'] { color: var(--accent-danger); }
.command-panel-copy { display: flex; flex-direction: column; gap: 1px; flex: 1; min-width: 0; }
.command-panel-copy strong { font-weight: 500; overflow-wrap: anywhere; }
.command-panel-copy small { font-size: 11px; line-height: 1.45; color: var(--text-muted); overflow-wrap: anywhere; }
.command-panel-value { max-width: 32%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--text-secondary); font-size: 11px; }
.command-panel-switch { width: 28px; height: 16px; flex-shrink: 0; display: flex; align-items: center; padding: 2px; border-radius: 999px; background: var(--border-muted); }
.command-panel-switch span { width: 12px; height: 12px; border-radius: 50%; background: var(--text-muted); }
.command-panel-switch.is-on { background: var(--text-primary); justify-content: flex-end; }
.command-panel-switch.is-on span { background: var(--bg-primary); }
.command-panel-footer { padding: 8px 12px; flex-shrink: 0; color: var(--text-muted); background: var(--surface-raised); font-size: 11px; line-height: 1.5; overflow-wrap: anywhere; }
.command-panel-footer.has-error, .command-panel-feedback { color: var(--accent-danger); }
.command-panel-note, .command-panel-feedback { margin: 6px 10px; font-size: 11px; line-height: 1.6; }
.command-panel-note { color: var(--text-muted); }
.command-panel-feedback button { color: inherit; background: transparent; border: 0; text-decoration: underline; cursor: pointer; }
</style>
