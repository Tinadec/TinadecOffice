<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch, type Component } from 'vue'
import { ArrowLeft, Bot, Check, ChevronRight, FileText, GitBranch, Image, Layers, ListChecks, MessageSquare, Network, Search, Settings, Sparkles, Users, Workflow, X } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { api, type AgentModeTopologyDto, type SpaceOptionsDto } from '@/api'
import type { PermissionLevel } from '@/types/mode'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { isCommandAvailable, slashCommands, type AppCommand, type CommandHost } from '@/lib/appCommands'
import { composerSettings, pageDepth, parentPage, permissionChoices, type ComposerPage, type PermissionRisk } from '@/lib/composerCommands'
import { defaultSpaceOptions } from '@/lib/composerSettings'
import { modeIcon, sortModes } from '@/lib/modePresentation'
import type { DefaultModel } from '@/composables/useDefaultModel'
import type { AgentModels, ModelChoice, NamedStrategy } from '@/composables/useAgentModels'
import type { AgentDirectoryItemDto } from '@/api'
import type { ReasoningEffortChoice } from '@/lib/reasoningEffort'
import ReasoningEffortSlider from './ReasoningEffortSlider.vue'

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
  /** The default model (chat route), offered as what an agent without its own model follows. */
  defaultModel?: DefaultModel
  /** The conversation agent's model, shown on the command list. */
  defaultModelLabel?: string
  /** Each agent's own model; the model page sets the conversation agent's, a page below the others'. */
  agentModels?: AgentModels
  /** The session pins the conversation agent to a model, which wins over the agent's own. */
  modelPinned?: boolean
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
  /** A model chosen for the conversation agent should take effect, so the session pin goes. */
  'clear-model-pin': []
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
/** The agent whose model the agent-model page sets. */
const modelAgentId = ref<string | null>(null)
const modelAgent = computed(() => props.agentModels?.agents.value.find(agent => agent.id === modelAgentId.value) ?? null)
/** "Back to the command list" only where back leads there; deeper pages go up one level. */
const backLabel = computed(() => t(pageDepth(page.value) > 1 ? 'common.back' : 'commandPanel.back'))
/** The agent pages wait for the list instead of reading as empty. */
const onModelPage = computed(() => page.value === 'model' || page.value === 'model-agents' || page.value === 'model-agent')
const agentListPending = computed(() => (page.value === 'model-agents' || page.value === 'model-agent')
  && props.agentModels?.loaded.value === false && !props.agentModels.error.value)
/** The agent list could not be read: said on the model pages, with a retry, instead of a silent fallback. */
const agentListFailed = computed(() => onModelPage.value && props.agentModels?.loaded.value === false && props.agentModels.error.value)
let loadSequence = 0
/**
 * The slider moves at once; whenever no write is running it shows the saved value again, so a
 * failed or dropped choice snaps back even when the saved value itself did not change.
 */
const reasoningDraft = ref<ReasoningEffortChoice>('')
watch(() => [props.defaultModel?.reasoningEffort.value ?? '', props.defaultModel?.busy.value ?? false] as const,
  ([value, busy]) => { if (!busy) reasoningDraft.value = value }, { immediate: true })
// Until the agent list is read the slider would point at the default model rather than the one the
// conversation agent runs on; after a failed read it does point there, as the fallback page does.
const showReasoning = computed(() => page.value === 'model'
  && (!props.agentModels || props.agentModels.loaded.value || props.agentModels.error.value)
  && Boolean(props.defaultModel?.reasoningSupported.value))
function stopSliderKeys(event: KeyboardEvent) { if (event.key !== 'Escape') event.stopPropagation() }
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
  if (props.open && page.value === next && next !== previous) animateContent(pageDepth(next) < pageDepth(previous) ? -8 : 8, previousHeight)
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
  emit('mode-label', selectedMode.value?.display_name ?? t('commandPanel.mode'), !!props.modeVersionId && !selectedMode.value && !loading.value, selectedMode.value ? modeIcon(selectedMode.value.slug) : Layers)
}, { immediate: true })
// A conversation without a mode gets the first published one (Solo when the pack has it) as
// soon as the catalog is known, so its runs never fall back to workspace-defaults' choice.
watch([availableModes, () => props.modeVersionId, loading], ([modes, current, busy]) => {
  if (props.spatial || busy || current || props.settingsSaving || !modes.length) return
  emit('update:modeVersionId', modes[0]!.latest_published_mode_version_id!)
})
const workflowName = computed(() => availableModes.value.find(mode => mode.latest_published_mode_version_id === space.value.workflow_mode_version_id)?.display_name)
const currentPermission = computed(() => permissionChoices.find(choice => choice.value === props.permission)!)
const permissionName = computed(() => t(currentPermission.value.label))
const title = computed(() => {
  if (page.value === 'root') return t('composer.commands')
  if (page.value === 'model-agents') return t('commandPanel.modelAgents')
  if (page.value === 'model-agent') return modelAgent.value?.display_name || modelAgent.value?.slug || t('commandPanel.modelAgents')
  return t(`commandPanel.${page.value}`)
})
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
  const parent = parentPage[page.value] ?? 'root'
  page.value = parent
  query.value = parent === 'root' ? rootQuery.value : ''
  index.value = 0
  void nextTick(() => {
    if (props.slashQuery != null) emit('focus-composer')
    else searchRef.value?.focus()
  })
}
function modeRows(workflow: boolean): Row[] {
  const selected = workflow ? space.value.workflow_mode_version_id : props.modeVersionId
  // Only a workflow can be "off". A conversation always has a real mode: "follow the default"
  // resolved to whatever workspace-defaults named, often nothing anyone would choose.
  const off: Row[] = workflow ? [{
    id: 'default', label: t('commandPanel.workflowOff'), icon: Workflow, checked: !selected, disabled: props.settingsSaving,
    run: () => emit('update:spaceOptions', { ...space.value, workflow_mode_version_id: null }),
  }] : []
  return [...off, ...availableModes.value.filter(mode => !workflow || mode.edges.length > 0).map(mode => ({
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
const manageModelsRow = (): Row => ({ id: 'manage-models', label: t('composer.modelSwitch.manage'), icon: Settings, run: () => emit('manage-models') })
function choiceName(choice: ModelChoice): string {
  return choice.model || props.defaultModel?.providerName(choice.provider_instance_id) || t('composer.pinnedModelUnknown')
}
function strategyName(strategy: NamedStrategy): string {
  return strategy.kind === 'fixed' ? choiceName(strategy.choice) : t('composer.agentModels.route', { purpose: strategy.purpose })
}
/** The agent's model in a word: its own, else the configured one, else "follow the default". */
function agentModelName(agent: AgentDirectoryItemDto): string {
  const agents = props.agentModels!
  const strategy = agents.ownStrategy(agent) ?? agents.configuredStrategy(agent)
  return strategy ? strategyName(strategy) : t('composer.agentModels.followDefault')
}
/** What "follow the default" leads to for this agent, so the row says it rather than implying the default model. */
function followHint(agent: AgentDirectoryItemDto, conversation: boolean): string {
  const configured = props.agentModels!.configuredStrategy(agent)
  if (configured) return t('composer.agentModels.followConfigured', { model: strategyName(configured) })
  if (conversation) return t('composer.agentModels.followDefaultHint', { model: props.defaultModel?.name.value || t('composer.modelSwitch.none') })
  return t('composer.agentModels.followDefaultOtherHint')
}
/**
 * One agent's choices: follow the default, every configured model and every CLI/ACP runtime. A
 * route given in the agent center shows as the current choice; nothing here replaces it unasked.
 */
function modelChoiceRows(agent: AgentDirectoryItemDto, conversation: boolean, choose: (choice: ModelChoice | null) => void): Row[] {
  const model = props.defaultModel
  const agents = props.agentModels!
  const busy = agents.busy.value || (model?.busy.value ?? false)
  const own = agents.ownStrategy(agent)
  const selected = own?.kind === 'fixed' ? own.choice : null
  const rows: Row[] = []
  if (own?.kind === 'route') rows.push({ id: 'own-route', label: strategyName(own), hint: t('composer.agentModels.routeHint'), icon: Network, checked: true, disabled: true, run: () => {} })
  rows.push({ id: 'follow-default', label: t('composer.agentModels.followDefault'), hint: followHint(agent, conversation), icon: Sparkles,
    keywords: 'default inherit 默认 跟随', checked: !own, disabled: busy, run: () => choose(null) })
  for (const row of model?.models.value ?? []) rows.push({
    id: `${row.provider_instance_id}:${row.model_id}`, label: row.display_name || row.model_id,
    hint: row.provider_display_name ?? undefined, keywords: `${row.model_id} ${row.provider_display_name ?? ''}`, icon: Sparkles,
    checked: selected?.provider_instance_id === row.provider_instance_id && selected.model === row.model_id, disabled: busy,
    run: () => choose({ provider_instance_id: row.provider_instance_id, model: row.model_id }),
  })
  for (const runtime of model?.runtimes.value ?? []) rows.push({
    id: `runtime:${runtime.provider_instance_id}`, label: runtime.display_name, hint: t('composer.agentModels.runtime'),
    keywords: `${runtime.display_name} cli acp`, icon: Bot,
    checked: selected?.provider_instance_id === runtime.provider_instance_id && !selected.model, disabled: busy,
    run: () => choose({ provider_instance_id: runtime.provider_instance_id, model: null }),
  })
  if (model?.loaded.value && !model.models.value.length && !model.runtimes.value.length)
    rows.push({ id: 'no-models', label: t('composer.modelSwitch.empty'), icon: Sparkles, disabled: true, run: () => {} })
  return rows
}
/**
 * The conversation agent's model, then the way to the other agents. Until the agent list is read
 * (or when the session's conversation agent is not in it) this page sets the default model, as it
 * always did, rather than offering nothing; a failed read says so above the rows.
 */
function modelPageRows(): Row[] {
  const agents = props.agentModels
  const conversation = agents?.loaded.value ? agents.conversationAgent.value : null
  if (!agents || !conversation) {
    const model = props.defaultModel
    const choices: Row[] = (model?.models.value ?? []).map(row => ({
      id: `${row.provider_instance_id}:${row.model_id}`, label: row.display_name || row.model_id,
      hint: row.provider_display_name ?? undefined, keywords: `${row.model_id} ${row.provider_display_name ?? ''}`, icon: Sparkles,
      checked: model?.isCurrent(row.provider_instance_id, row.model_id) ?? false, disabled: model?.busy.value ?? false,
      run: () => { void model?.choose(row.provider_instance_id, row.model_id) },
    }))
    if (model?.loaded.value && !choices.length)
      choices.push({ id: 'no-models', label: t('composer.modelSwitch.empty'), icon: Sparkles, disabled: true, run: () => {} })
    return [...choices, manageModelsRow()]
  }
  const rows = modelChoiceRows(conversation, true, choice => {
    // The session pin wins over the agent's own model, so it goes — but only once the choice is saved.
    void agents.setModel(conversation, choice).then(saved => { if (saved && props.modelPinned) emit('clear-model-pin') })
  })
  const ownModels = agents.otherAgents.value.filter(agent => agents.ownStrategy(agent)).length
  rows.push({ id: 'model-agents', label: t('commandPanel.modelAgents'), hint: t('commandPanel.modelAgentsHint'), icon: Users,
    keywords: 'agents executor 智能体 执行', value: ownModels ? t('composer.agentModels.ownCount', { count: ownModels }) : undefined,
    submenu: true, testId: 'command-panel-model-agents', run: () => selectPage('model-agents') })
  rows.push(manageModelsRow())
  return rows
}
function otherAgentRows(): Row[] {
  const agents = props.agentModels
  if (!agents?.loaded.value) return []
  return agents.otherAgents.value.map(agent => ({
    id: `agent:${agent.id}`, label: agent.display_name || agent.slug,
    hint: agents.usageInMode(agent) ? t('composer.agentModels.inMode') : undefined, keywords: agent.slug, icon: Bot,
    value: agentModelName(agent), submenu: true, testId: `command-panel-agent-${agent.slug}`,
    run: () => { modelAgentId.value = agent.id; selectPage('model-agent') },
  }))
}
const allRows = computed<Row[]>(() => {
  if (page.value === 'permission') return permissionChoices.map(choice => ({
    id: choice.value, label: t(choice.label), hint: t(choice.hint), icon: choice.icon, risk: choice.risk, checked: props.permission === choice.value,
    disabled: props.settingsSaving, run: () => emit('update:permission', choice.value),
  }))
  if (page.value === 'mode' || page.value === 'workflow') return modeRows(page.value === 'workflow')
  if (page.value === 'model') return modelPageRows()
  if (page.value === 'model-agents') return otherAgentRows()
  if (page.value === 'model-agent') {
    const agents = props.agentModels
    const agent = modelAgent.value
    if (!agents?.loaded.value || !agent) return []
    return modelChoiceRows(agent, false, choice => { void agents.setModel(agent, choice) })
  }
  const rows: Row[] = [
    { id: 'image', label: t('chat.addImage'), hint: props.canAttach ? undefined : t('commandPanel.attachmentsBusy'), keywords: 'image picture 图片', icon: Image, disabled: !props.canAttach, testId: 'composer-attach-image', run: () => emit('attach', 'image/*') },
    { id: 'file', label: t('chat.addFile'), hint: props.canAttach ? undefined : t('commandPanel.attachmentsBusy'), keywords: 'file attachment 文件 附件', icon: FileText, disabled: !props.canAttach, testId: 'composer-attach-file', run: () => emit('attach', '') },
  ]
  for (const setting of composerSettings) {
    if ('surface' in setting && (setting.surface === 'space') !== Boolean(props.spatial)) continue
    const toggle = 'toggle' in setting ? setting.toggle : null
    const submenu = 'page' in setting ? setting.page : null
    const currentValue = setting.id === 'model' ? props.defaultModelLabel : setting.id === 'mode' ? selectedMode.value?.display_name ?? t('commandPanel.mode') : setting.id === 'permission' ? permissionName.value : setting.id === 'workflow' ? workflowName.value ?? (space.value.workflow_mode_version_id ? t('commandPanel.unavailableSelection') : t('commandPanel.off')) : undefined
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
  void props.agentModels?.load()
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
        <button v-if="page !== 'root'" class="command-panel-icon command-panel-back" :aria-label="backLabel" :title="t('commandPanel.backHint')" @click="back"><ArrowLeft :size="16" /><span>{{ backLabel }}</span></button>
        <strong>{{ title }}</strong>
        <button class="command-panel-icon command-panel-close" :aria-label="t('commandPanel.close')" @click="emit('close')"><X :size="15" /></button>
      </header>
      <label class="command-panel-search">
        <Search :size="15" aria-hidden="true" />
        <input ref="searchRef" :value="activeQuery" :readonly="page === 'root' && slashQuery != null" :placeholder="t('commandPanel.search')" :aria-label="t('commandPanel.search')" @input="query = ($event.target as HTMLInputElement).value" />
      </label>
      <div class="command-panel-list">
        <p v-if="catalogError" class="command-panel-feedback" role="alert">{{ t('commandPanel.catalogError') }} <button @click="loadCatalogs">{{ t('commandPanel.retry') }}</button></p>
        <p v-if="agentListFailed" class="command-panel-feedback" role="alert" data-testid="command-panel-agents-failed">{{ t(page === 'model' ? 'composer.agentModels.loadFailedFallback' : 'composer.agentModels.loadFailed') }} <button @click="agentModels?.load()">{{ t('commandPanel.retry') }}</button></p>
        <p v-if="(loading || agentListPending) && page !== 'root'" class="command-panel-note" role="status">{{ t('commandPanel.loading') }}</p>
        <button v-for="(row, rowIndex) in rows" :key="row.id" class="command-panel-row" :class="{ 'is-highlighted': rowIndex === index, 'is-selected': row.checked, 'mode-selector-item': page === 'mode', active: page === 'mode' && row.checked }" :role="row.switch ? 'switch' : undefined" :aria-checked="row.switch ? row.checked : undefined" :aria-pressed="!row.switch && row.checked !== undefined ? row.checked : undefined" :aria-label="row.risk && row.risk !== 'neutral' ? `${row.label} · ${t(`commandPanel.risk.${row.risk}`)}` : undefined" :disabled="row.disabled" :data-testid="row.testId ?? `command-panel-${row.id}`" :data-row-index="rowIndex" :title="row.hint" @mouseenter="index = rowIndex" @focus="index = rowIndex" @click="activate(row)">
          <component :is="row.icon" v-if="row.icon" :size="16" class="command-panel-row-icon" :data-risk="row.risk" aria-hidden="true" />
          <span class="command-panel-copy"><strong>{{ row.label }}</strong><small v-if="row.hint">{{ row.hint }}</small></span>
          <span v-if="row.switch" class="command-panel-switch" :class="{ 'is-on': row.checked }" aria-hidden="true"><span /></span>
          <template v-else><span v-if="row.value" class="command-panel-value">{{ row.value }}</span><ChevronRight v-if="row.submenu" :size="14" aria-hidden="true" /><Check v-else-if="row.checked" :size="16" aria-hidden="true" /></template>
        </button>
        <p v-if="!rows.length && !agentListPending && !agentListFailed" class="command-panel-note">{{ t(page === 'model-agents' && !activeQuery ? 'composer.agentModels.none' : 'commandPanel.empty') }}</p>
        <button v-if="page === 'root' && slashQuery && !rows.length" class="command-panel-row" data-testid="command-panel-send-as-text" @click="emit('send-as-text')"><FileText :size="16" class="command-panel-row-icon" aria-hidden="true" />{{ t('commandPanel.sendAsText') }}</button>
        <p v-if="page === 'workflow'" class="command-panel-note">{{ t('commandPanel.workflowRequirement') }}</p>
      </div>
      <!-- The slider's own keys (arrows, Home/End) stay with it instead of moving the row highlight. -->
      <div v-if="showReasoning" class="command-panel-reasoning" data-testid="command-panel-reasoning" @keydown="stopSliderKeys">
        <label for="command-panel-reasoning-slider" :title="t('composer.reasoning.hint', { model: defaultModel?.reasoningTarget.value?.model ?? defaultModel?.current.value?.model ?? '' })">{{ t('composer.reasoning.label') }}</label>
        <ReasoningEffortSlider id="command-panel-reasoning-slider" v-model="reasoningDraft" @commit="defaultModel?.setReasoningEffort($event)" />
      </div>
      </div>
      <footer class="command-panel-footer" :class="{ 'has-error': settingsError }" :role="settingsError ? 'alert' : 'status'">{{ settingsError || (settingsSaving ? t('commandPanel.saving') : t(page === 'model' || page === 'model-agents' || page === 'model-agent' ? 'commandPanel.modelFooter' : 'commandPanel.nextTask')) }}</footer>
    </section>
  </Teleport>
</template>

<style scoped>
.composer-command-panel { --command-panel-transition-duration: var(--durationFast, 150ms); --command-panel-transition-easing: var(--curveDecelerateMid, cubic-bezier(0.2, 0, 0, 1)); z-index: 9999; display: flex; flex-direction: column; overflow: hidden; border-radius: 16px; background: var(--surface-section); box-shadow: var(--shadow-panel); color: var(--text-primary); font-size: 12px; }
.command-panel-content { display: flex; flex-direction: column; min-height: 0; flex: 1 1 auto; }
.command-panel-header { display: flex; align-items: center; gap: 8px; padding: 10px 12px 2px; flex-shrink: 0; }
.command-panel-header strong { font-weight: 600; }
.command-panel-icon { width: 28px; height: 28px; display: grid; place-items: center; background: transparent; border: 0; border-radius: 7px; color: var(--text-secondary); cursor: pointer; }
.command-panel-icon:hover { background: var(--surface-hover); }
.command-panel-back { width: auto; display: inline-flex; gap: 4px; padding: 0 6px; }
.command-panel-close { margin-left: auto; }
.command-panel-search { display: flex; gap: 8px; align-items: center; color: var(--text-muted); margin: 4px 12px 8px; padding: 8px; border-radius: 8px; background: var(--surface-input); flex-shrink: 0; font-weight: 400; }
.command-panel-search input { min-width: 0; width: 100%; height: auto; min-height: 0; padding: 0; border-radius: 0; line-height: 1.5; color: var(--text-primary); background: transparent; border: 0; box-shadow: none; outline: none; font: inherit; }
.command-panel-reasoning { display: flex; flex-direction: column; gap: 4px; flex-shrink: 0; margin: 0 12px 8px; padding: 10px 10px 4px; border-radius: 9px; background: var(--surface-input); }
.command-panel-reasoning label { color: var(--text-secondary); font-weight: 500; }
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
