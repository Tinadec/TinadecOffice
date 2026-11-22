<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Activity,
  Check,
  Copy,
  Eye,
  FileText,
  GitBranch,
  History,
  Layers,
  Plus,
  RefreshCw,
  Save,
  Search,
  ThumbsDown,
  ThumbsUp,
  Trash2,
  Undo2,
  Workflow,
} from '@lucide/vue'
import { UiBadge, UiButton, UiCard, UiInput, UiLabel, UiSelectField, UiSheet, UiSwitch } from '@/components/ui'
import PromptPipelineCanvas from '@/components/canvas/PromptPipelineCanvas.vue'
import {
  api as baseApi,
  type AgentDirectoryItemDto,
  type AgentModeDto,
  type PackI18n,
  type PromptContextPreviewInput,
  type PromptContextPreviewNormalizedDto,
  type PromptFragmentDto,
  type PromptFragmentEffectivenessDto,
  type PromptFragmentVersionDto,
  type PromptPipelineDto,
  type PromptPipelineEdgeDto,
  type PromptPipelineNodeDto,
} from '@/api'
import { scopedApi } from '@/lib/storageScope'
const api = scopedApi(baseApi, () => 'user')
import { useNotifications } from '@/composables/useNotifications'
import { useHostAccess } from '@/lib/hostAccess'
import { consumeRequest, pendingPromptId } from '@/lib/pageRequests'
import { pickLocalized } from '@/lib/packI18n'

const { t, te, locale } = useI18n()
const definitionStatusLabel = (status: string) => (te(`agentCenter.filter.${status}`) ? t(`agentCenter.filter.${status}`) : status)
const { notify, confirm } = useNotifications()
const { canAccessBackend } = useHostAccess()

// ── shared view state ──
const promptCanvasMode = ref<'fragments' | 'canvas'>('fragments')
const fragLoading = ref(false)
const fragBusy = ref(false)
const agents = ref<AgentDirectoryItemDto[]>([])
const modes = ref<AgentModeDto[]>([])

function agentDisplayName(agent: AgentDirectoryItemDto): string {
  const baseline = agent.display_name || agent.slug || agent.id
  return pickLocalized(agent.i18n, locale.value, 'display_name', baseline) ?? baseline
}

function modeDisplayName(mode: AgentModeDto): string {
  return pickLocalized(mode.i18n, locale.value, 'display_name', mode.display_name) ?? mode.display_name
}

function pipelineDisplayName(pipeline: PromptPipelineDto): string {
  const i18n = (pipeline as PromptPipelineDto & { i18n?: PackI18n | null }).i18n
  const baseline = pipeline.display_name ?? pipeline.name ?? pipeline.title ?? pipeline.slug ?? pipeline.id.slice(0, 8)
  return pickLocalized(i18n, locale.value, 'display_name', baseline) ?? baseline
}

function pipelineDescription(pipeline: PromptPipelineDto): string {
  const i18n = (pipeline as PromptPipelineDto & { i18n?: PackI18n | null }).i18n
  return pickLocalized(i18n, locale.value, 'description', pipeline.description) ?? ''
}

/** structuredClone throws DataCloneError on Vue reactive Proxies; JSON DTOs clone safely this way. */
function plainClone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

// ── pipelines (canvas view) ──
const pipelines = ref<PromptPipelineDto[]>([])
const pipelineCanvasSelectedId = ref<string | null>(null)
const pipelineDraftName = ref('')
const pipelineDraftDesc = ref('')
const pipelineDraftNodes = ref<PromptPipelineNodeDto[]>([])
const pipelineDraftEdges = ref<PromptPipelineEdgeDto[]>([])
const pipelineBaseline = ref('')
const pipelineSaving = ref(false)
const pipelinePublishing = ref(false)
const showCreatePipeline = ref(false)
const newPipelineForm = ref({ display_name: '', slug: '', description: '' })
const selectedNodeId = ref<string | null>(null)
const selectedEdgeId = ref<string | null>(null)

const pipelineCanvasPipeline = computed(() =>
  pipelineCanvasSelectedId.value ? pipelines.value.find((p) => p.id === pipelineCanvasSelectedId.value) ?? null : null,
)

const activePipelineForCanvas = computed<PromptPipelineDto | null>(() => {
  const base = pipelineCanvasPipeline.value
  if (!base) return null
  return {
    ...base,
    display_name: pipelineDraftName.value || pipelineDisplayName(base),
    description: pipelineDraftDesc.value,
    graph: {
      nodes: pipelineDraftNodes.value,
      edges: pipelineDraftEdges.value,
      ...(base.graph?.canvas_layout ? { canvas_layout: base.graph.canvas_layout } : {}),
    },
    nodes: pipelineDraftNodes.value,
    edges: pipelineDraftEdges.value,
  }
})

const isPipelineReadonly = computed(() => {
  const p = pipelineCanvasPipeline.value
  if (!p) return true
  return Boolean(p.pack_managed || p.status === 'archived')
})

const pipelineSignal = computed(() =>
  JSON.stringify({
    name: pipelineDraftName.value,
    description: pipelineDraftDesc.value,
    nodes: pipelineDraftNodes.value,
    edges: pipelineDraftEdges.value,
  }),
)
const pipelineDirty = computed(() => Boolean(pipelineCanvasPipeline.value) && pipelineSignal.value !== pipelineBaseline.value)

function syncPipelineDraft(p: PromptPipelineDto | null) {
  selectedNodeId.value = null
  selectedEdgeId.value = null
  if (!p) {
    pipelineDraftName.value = ''
    pipelineDraftDesc.value = ''
    pipelineDraftNodes.value = []
    pipelineDraftEdges.value = []
    pipelineBaseline.value = ''
    return
  }
  const nodes = plainClone(p.graph?.nodes ?? p.nodes ?? [])
  const edges = plainClone(p.graph?.edges ?? p.edges ?? [])
  pipelineDraftName.value = p.display_name ?? p.name ?? p.title ?? p.slug ?? ''
  pipelineDraftDesc.value = p.description ?? ''
  pipelineDraftNodes.value = nodes
  pipelineDraftEdges.value = edges
  pipelineBaseline.value = JSON.stringify({
    name: pipelineDraftName.value,
    description: pipelineDraftDesc.value,
    nodes,
    edges,
  })
}

watch(pipelineCanvasSelectedId, (id) => {
  const found = id ? pipelines.value.find((p) => p.id === id) ?? null : null
  syncPipelineDraft(found)
})

const selectedNode = computed<PromptPipelineNodeDto | null>(() =>
  selectedNodeId.value ? pipelineDraftNodes.value.find((n) => n.id === selectedNodeId.value) ?? null : null,
)
const selectedEdge = computed<PromptPipelineEdgeDto | null>(() =>
  selectedEdgeId.value
    ? pipelineDraftEdges.value.find((e) => (e.id || `${e.source}->${e.target}`) === selectedEdgeId.value) ?? null
    : null,
)

function nodeTypeOf(node: PromptPipelineNodeDto): string {
  return String(node.type ?? node.kind ?? 'template').toLowerCase()
}

function localizedPackTemplate(node: PromptPipelineNodeDto): string | null {
  const p = pipelineCanvasPipeline.value
  if (!p?.pack_managed) return null
  const i18n = (p as PromptPipelineDto & { i18n?: PackI18n | null }).i18n
  const overlay = i18n?.[locale.value] ?? i18n?.en
  const tpl = overlay?.templates?.[node.id]
  return typeof tpl === 'string' && tpl.trim() ? tpl : null
}

function patchSelectedNode(mutator: (node: PromptPipelineNodeDto) => PromptPipelineNodeDto) {
  if (!selectedNodeId.value || isPipelineReadonly.value) return
  pipelineDraftNodes.value = pipelineDraftNodes.value.map((n) =>
    n.id === selectedNodeId.value ? mutator(plainClone(n)) : n,
  )
}

function patchSelectedEdgeLabel(label: string) {
  if (!selectedEdgeId.value || isPipelineReadonly.value) return
  pipelineDraftEdges.value = pipelineDraftEdges.value.map((e) =>
    (e.id || `${e.source}->${e.target}`) === selectedEdgeId.value ? { ...e, label } : e,
  )
}

async function loadPipelines() {
  if (!canAccessBackend.value) return
  try {
    const list = await api.listPromptPipelines()
    pipelines.value = Array.isArray(list) ? list : []
    if (!pipelineCanvasSelectedId.value && pipelines.value[0]) {
      pipelineCanvasSelectedId.value = pipelines.value[0].id
    } else if (pipelineCanvasSelectedId.value) {
      const current = pipelines.value.find((p) => p.id === pipelineCanvasSelectedId.value) ?? null
      syncPipelineDraft(current)
    }
  } catch {
    pipelines.value = []
  }
}

function handlePipelineCanvasUpdateNodes(payload: PromptPipelineNodeDto[]) {
  if (isPipelineReadonly.value) return
  pipelineDraftNodes.value = payload
}

function handlePipelineCanvasUpdateEdges(payload: PromptPipelineEdgeDto[]) {
  if (isPipelineReadonly.value) return
  pipelineDraftEdges.value = payload
}

function handleSelectNode(node: PromptPipelineNodeDto | null) {
  selectedNodeId.value = node?.id ?? null
  if (node) selectedEdgeId.value = null
}

function handleSelectEdge(edge: PromptPipelineEdgeDto | null) {
  selectedEdgeId.value = edge ? (edge.id || `${edge.source}->${edge.target}`) : null
  if (edge) selectedNodeId.value = null
}

async function savePipelineDraft(): Promise<boolean> {
  const target = pipelineCanvasPipeline.value
  if (!target || isPipelineReadonly.value || !canAccessBackend.value) return false
  pipelineSaving.value = true
  try {
    const updated = await api.updatePromptPipelineDraft(
      target.id,
      {
        display_name: pipelineDraftName.value.trim() || target.display_name || target.id,
        description: pipelineDraftDesc.value,
        graph: {
          nodes: pipelineDraftNodes.value,
          edges: pipelineDraftEdges.value,
          ...(target.graph?.canvas_layout ? { canvas_layout: target.graph.canvas_layout } : {}),
        },
      },
      target.revision ?? target.etag ?? null,
    )
    pipelines.value = pipelines.value.map((p) => (p.id === updated.id ? updated : p))
    syncPipelineDraft(updated)
    notify.success({ message: t('settings.promptStudio.pipelineDraftSaved') })
    return true
  } catch (e) {
    notify.error(e, { title: t('settings.promptEngineering') })
    return false
  } finally {
    pipelineSaving.value = false
  }
}

async function publishPipeline(pipelineId: string) {
  if (!canAccessBackend.value) return
  const target = pipelines.value.find((p) => p.id === pipelineId)
  if (!target || target.pack_managed) return
  pipelinePublishing.value = true
  try {
    if (pipelineId === pipelineCanvasSelectedId.value && pipelineDirty.value) {
      const ok = await savePipelineDraft()
      if (!ok) return
    }
    const fresh = pipelines.value.find((p) => p.id === pipelineId)
    await api.publishPromptPipeline(pipelineId, fresh?.revision ?? fresh?.etag ?? null)
    notify.success({ message: t('settings.pipelinePublished') })
    await loadPipelines()
  } catch (e) {
    notify.error(e, { title: t('settings.promptEngineering') })
  } finally {
    pipelinePublishing.value = false
  }
}

async function createPipelineDraft() {
  const name = newPipelineForm.value.display_name.trim()
  if (!name || !canAccessBackend.value) return
  pipelineSaving.value = true
  try {
    const created = await api.createPromptPipelineDraft({
      display_name: name,
      slug: newPipelineForm.value.slug.trim() || undefined,
      description: newPipelineForm.value.description.trim() || undefined,
      graph: {
        nodes: [
          { id: 'template_1', type: 'template', label: 'template_1', position: { x: 60, y: 80 }, config: { content: '' } },
          { id: 'assemble_1', type: 'assemble', label: 'assemble_1', position: { x: 340, y: 80 }, config: { separator: '\n\n' } },
        ],
        edges: [{ id: 'e-template_1-assemble_1', source: 'template_1', target: 'assemble_1' }],
      },
    })
    showCreatePipeline.value = false
    newPipelineForm.value = { display_name: '', slug: '', description: '' }
    await loadPipelines()
    pipelineCanvasSelectedId.value = created.id
    notify.success({ message: t('settings.promptStudio.pipelineCreated') })
  } catch (e) {
    notify.error(e, { title: t('settings.promptEngineering') })
  } finally {
    pipelineSaving.value = false
  }
}

async function cloneCurrentPipeline(pipelineId?: string) {
  const id = pipelineId ?? pipelineCanvasSelectedId.value
  if (!id || !canAccessBackend.value) return
  pipelineSaving.value = true
  try {
    const cloned = await api.clonePromptPipeline(id)
    await loadPipelines()
    pipelineCanvasSelectedId.value = cloned.id
    notify.success({ message: t('settings.promptStudio.pipelineCloned') })
  } catch (e) {
    notify.error(e, { title: t('settings.promptEngineering') })
  } finally {
    pipelineSaving.value = false
  }
}

// ── fragments (list/detail view) ──
const fragments = ref<PromptFragmentDto[]>([])
const fragQuery = ref('')
const fragEnabledFilter = ref<'all' | 'enabled' | 'disabled'>('all')
const selectedFragmentId = ref('')
const creatingFragment = ref(false)
const requestedPromptId = ref<string | null>(null)
let promptNavigationGeneration = 0
onBeforeUnmount(() => { promptNavigationGeneration++ })
const fragmentVersions = ref<PromptFragmentVersionDto[]>([])
const fragmentEffectiveness = ref<PromptFragmentEffectivenessDto | null>(null)

const fragmentDraft = ref({
  key: '',
  title: '',
  scope: 'global',
  target_agent_id: '' as string,
  category: 'system',
  content: '',
  priority: 50,
  enabled: true,
})

const filteredFragments = computed(() => {
  const q = fragQuery.value.trim().toLowerCase()
  return fragments.value.filter((f) => {
    if (fragEnabledFilter.value === 'enabled' && !f.enabled) return false
    if (fragEnabledFilter.value === 'disabled' && f.enabled) return false
    if (!q) return true
    return `${f.title} ${f.key} ${f.category} ${f.scope}`.toLowerCase().includes(q)
  })
})
const selectedFragment = computed(() =>
  creatingFragment.value ? null : fragments.value.find((f) => f.id === selectedFragmentId.value) ?? null,
)
const isReadonlyFragment = computed(() => Boolean(selectedFragment.value?.is_builtin))
const sortedVersions = computed(() => [...fragmentVersions.value].sort((a, b) => b.version - a.version))

function syncFragmentDraft(fragment: PromptFragmentDto | null) {
  if (!fragment) return
  fragmentDraft.value = {
    key: fragment.key,
    title: fragment.title,
    scope: fragment.scope || 'global',
    target_agent_id: fragment.target_agent_id ?? '',
    category: fragment.category || 'system',
    content: fragment.content,
    priority: fragment.priority ?? 50,
    enabled: fragment.enabled ?? true,
  }
}

function startNewFragment() {
  creatingFragment.value = true
  selectedFragmentId.value = ''
  fragmentVersions.value = []
  fragmentEffectiveness.value = null
  fragmentDraft.value = {
    key: `fragment_${Date.now().toString(36)}`,
    title: '',
    scope: 'global',
    target_agent_id: '',
    category: 'system',
    content: '',
    priority: 50,
    enabled: true,
  }
}

let fragmentsRead: Promise<void> | null = null
function loadFragments(): Promise<void> {
  return fragmentsRead ??= loadFragmentsData().finally(() => { fragmentsRead = null })
}
async function loadFragmentsData() {
  if (!canAccessBackend.value) return
  fragLoading.value = true
  try {
    const fragmentList = await api.listPromptFragments()
    fragments.value = Array.isArray(fragmentList) ? fragmentList : []
    if (requestedPromptId.value) return
    if (creatingFragment.value) return
    const selected = fragments.value.find((fragment) => fragment.id === selectedFragmentId.value)
    if (selected) await selectFragment(selected)
    else if (!selectedFragmentId.value && fragments.value[0]) await selectFragment(fragments.value[0])
    else if (selectedFragmentId.value) {
      selectedFragmentId.value = ''
      notify.error(t('app.loadFailedMessage'), { title: t('settings.fragmentDetailFailed'), key: 'search-navigation' })
    }
  } catch (err) {
    notify.error(err, { title: t('settings.promptLoadFailed') })
  } finally {
    fragLoading.value = false
  }
}

consumeRequest(pendingPromptId, async (promptId) => {
  const generation = ++promptNavigationGeneration
  promptCanvasMode.value = 'fragments'
  creatingFragment.value = false
  fragQuery.value = ''
  fragEnabledFilter.value = 'all'
  selectedFragmentId.value = promptId
  requestedPromptId.value = promptId
  if (!fragments.value.some((item) => item.id === promptId)) await loadFragments()
  if (generation !== promptNavigationGeneration) return
  const fragment = fragments.value.find((item) => item.id === promptId)
  requestedPromptId.value = null
  if (fragment) void selectFragment(fragment)
  else {
    selectedFragmentId.value = ''
    notify.error(t('app.loadFailedMessage'), { title: t('settings.fragmentDetailFailed'), key: 'search-navigation' })
  }
})

async function selectFragment(fragment: PromptFragmentDto) {
  creatingFragment.value = false
  const fid = fragment.id
  selectedFragmentId.value = fid
  syncFragmentDraft(fragment)
  fragmentVersions.value = []
  fragmentEffectiveness.value = null
  try {
    const versionList = await api.listPromptVersions(fid)
    if (selectedFragmentId.value !== fid) return
    fragmentVersions.value = Array.isArray(versionList) ? versionList : []
    const eff = await api.getPromptFragmentEffectiveness(fid).catch(() => null)
    if (selectedFragmentId.value !== fid) return
    fragmentEffectiveness.value = eff as PromptFragmentEffectivenessDto | null
  } catch (e) {
    if (selectedFragmentId.value !== fid) return
    notify.error(e, { title: t('settings.fragmentDetailFailed') })
  }
}

async function saveFragmentForm() {
  if (!canAccessBackend.value) return
  if (selectedFragment.value?.is_builtin) return
  const payload = {
    key: fragmentDraft.value.key.trim(),
    title: fragmentDraft.value.title.trim() || fragmentDraft.value.key.trim(),
    scope: fragmentDraft.value.scope || 'global',
    target_agent_id: fragmentDraft.value.scope === 'agent' ? (fragmentDraft.value.target_agent_id.trim() || null) : null,
    category: fragmentDraft.value.category.trim() || 'system',
    content: fragmentDraft.value.content,
    priority: Number(fragmentDraft.value.priority) || 0,
    enabled: fragmentDraft.value.enabled,
  }
  if (!payload.key) return

  fragBusy.value = true
  try {
    if (creatingFragment.value || !selectedFragment.value) {
      const created = await api.createPromptFragment(payload)
      creatingFragment.value = false
      await loadFragments()
      const fresh = fragments.value.find((item) => item.id === created.id) ?? created
      await selectFragment(fresh)
      notify.success({ message: t('settings.promptStudio.fragmentCreated') })
    } else {
      const saved = await api.savePromptFragment(
        selectedFragment.value.id,
        payload,
        selectedFragment.value.revision ?? null,
      )
      await loadFragments()
      const fresh = fragments.value.find((item) => item.id === saved.id) ?? saved
      await selectFragment(fresh)
      notify.success({ message: t('settings.promptStudio.fragmentSaved') })
    }
  } catch (e) {
    notify.error(e, { title: t('settings.promptEngineering') })
  } finally {
    fragBusy.value = false
  }
}

async function toggleFragmentEnabled(enabled: boolean) {
  fragmentDraft.value.enabled = enabled
  if (selectedFragment.value && !selectedFragment.value.is_builtin && !creatingFragment.value) {
    await saveFragmentForm()
  }
}

async function cloneCurrentFragment() {
  if (!selectedFragment.value || !canAccessBackend.value) return
  fragBusy.value = true
  try {
    const cloned = await api.clonePrompt(selectedFragment.value.id)
    await loadFragments()
    const fresh = fragments.value.find((item) => item.id === cloned.id) ?? cloned
    await selectFragment(fresh)
    notify.success({ message: t('settings.promptStudio.fragmentCloned') })
  } catch (e) {
    notify.error(e, { title: t('settings.promptEngineering') })
  } finally {
    fragBusy.value = false
  }
}

async function deleteCurrentFragment() {
  const target = selectedFragment.value
  if (!target || target.is_builtin || !canAccessBackend.value) return
  const ok = await confirm({
    title: t('settings.promptStudio.deleteFragment'),
    message: t('settings.promptStudio.deleteFragmentConfirm', { title: target.title }),
    confirmLabel: t('settings.promptStudio.deleteFragment'),
    cancelLabel: t('common.cancel'),
    destructive: true,
  })
  if (!ok) return
  fragBusy.value = true
  try {
    await api.deletePromptFragment(target.id)
    selectedFragmentId.value = ''
    fragmentVersions.value = []
    fragmentEffectiveness.value = null
    await loadFragments()
    notify.success({ message: t('settings.promptStudio.fragmentDeleted') })
  } catch (e) {
    notify.error(e, { title: t('settings.promptEngineering') })
  } finally {
    fragBusy.value = false
  }
}

async function rollbackVersion(version: number) {
  if (!selectedFragment.value || !canAccessBackend.value) return
  fragBusy.value = true
  try {
    await api.rollbackPrompt(selectedFragment.value.id, version)
    notify.success(t('settings.rollbackDone', { version }))
    await loadFragments()
    const fresh = fragments.value.find((item) => item.id === selectedFragment.value?.id)
    if (fresh) await selectFragment(fresh)
  } catch (e) {
    notify.error(e, { title: t('settings.rollbackFailed') })
  } finally {
    fragBusy.value = false
  }
}

// ── Live Context Preview & Per-Layer Token Breakdown ──
const promptPreview = ref<PromptContextPreviewNormalizedDto | null>(null)
const promptPreviewAgentId = ref('meeting')
const promptPreviewModeId = ref('')
const promptPreviewPipelineId = ref('')
const promptPreviewSessionId = ref('')
const includeDraftInPreview = ref(true)
const previewBusy = ref(false)
const expandedLayers = ref<Record<string, boolean>>({})
const promptCopied = ref(false)

const previewAgentOptions = computed(() => {
  const list = agents.value.map((a) => ({ value: a.slug || a.id, label: `${agentDisplayName(a)} (${a.slug || a.id.slice(0, 8)})` }))
  if (!list.some((opt) => opt.value === promptPreviewAgentId.value) && promptPreviewAgentId.value) {
    list.unshift({ value: promptPreviewAgentId.value, label: promptPreviewAgentId.value })
  }
  return list
})
const previewModeOptions = computed(() => [
  { value: '', label: t('common.none') },
  ...modes.value.filter((m) => m.status !== 'archived').map((m) => ({ value: m.id, label: modeDisplayName(m) })),
])
const previewPipelineOptions = computed(() => [
  { value: '', label: t('common.none') },
  ...pipelines.value.map((p) => ({ value: p.id, label: pipelineDisplayName(p) })),
])

const previewLayers = computed(() => promptPreview.value?.layers ?? [])
const previewTotalTokens = computed(() =>
  Math.max(
    1,
    promptPreview.value?.total_estimated_tokens ??
      previewLayers.value.reduce((acc, layer) => acc + (layer.estimated_tokens || 0), 0),
  ),
)

function layerPercent(tokens: number): number {
  return Math.max(2, Math.round((tokens / previewTotalTokens.value) * 100))
}

function toggleLayerExpanded(id: string) {
  expandedLayers.value = { ...expandedLayers.value, [id]: !expandedLayers.value[id] }
}

async function copyAssembledPrompt() {
  const text = promptPreview.value?.assembled_prompt ?? ''
  if (!text) return
  try {
    await navigator.clipboard?.writeText(text)
    promptCopied.value = true
    window.setTimeout(() => { promptCopied.value = false }, 1500)
  } catch {
    /* clipboard may be unavailable in test DOM */
  }
}

async function generatePromptPreview() {
  if (!canAccessBackend.value) return
  previewBusy.value = true
  try {
    const selectedMode = modes.value.find((m) => m.id === promptPreviewModeId.value)
    const input: PromptContextPreviewInput = {
      agent_id: promptPreviewAgentId.value.trim() || 'meeting',
      mode_id: promptPreviewModeId.value || undefined,
      mode: selectedMode?.slug ?? (promptPreviewModeId.value || undefined),
      mode_version_id: selectedMode?.latest_published_mode_version_id ?? undefined,
      pipeline_id: promptPreviewPipelineId.value || undefined,
      session_id: promptPreviewSessionId.value.trim() || undefined,
    }
    if (includeDraftInPreview.value) {
      if (promptCanvasMode.value === 'canvas' && activePipelineForCanvas.value?.graph) {
        input.draft_pipeline_graph = activePipelineForCanvas.value.graph
      } else if (promptCanvasMode.value === 'fragments' && selectedFragment.value) {
        input.include_fragment_ids = [selectedFragment.value.id]
      }
    }
    promptPreview.value = await api.previewPromptContext(input)
  } catch (e) {
    notify.error(e, { title: t('settings.previewFailed') })
  } finally {
    previewBusy.value = false
  }
}

async function loadMetaCatalog() {
  if (!canAccessBackend.value) return
  const [agentList, modeList] = await Promise.all([
    api.listAgents().catch(() => [] as AgentDirectoryItemDto[]),
    api.listAgentModes().catch(() => [] as AgentModeDto[]),
  ])
  agents.value = Array.isArray(agentList) ? agentList : []
  modes.value = Array.isArray(modeList) ? modeList : []
  if (agents.value.length > 0 && !agents.value.some((a) => a.slug === promptPreviewAgentId.value || a.id === promptPreviewAgentId.value)) {
    const meeting = agents.value.find((a) => a.slug === 'meeting') ?? agents.value[0]
    if (meeting) promptPreviewAgentId.value = meeting.slug || meeting.id
  }
}

async function refreshAll() {
  if (!canAccessBackend.value) return
  await Promise.all([loadMetaCatalog(), loadPipelines(), loadFragments()])
}

onMounted(() => {
  if (canAccessBackend.value) void refreshAll()
})
watch(canAccessBackend, (ready) => {
  if (ready) void refreshAll()
})

defineExpose({ refreshAll })
</script>

<template>
  <section class="center-resource-section agent-prompts-panel" data-testid="prompt-engineering-workbench">
    <div class="center-resource-heading">
      <div>
        <h3>{{ t('settings.promptEngineering') }}</h3>
        <p>{{ t('settings.promptsPanelHint') }}</p>
      </div>
      <div class="ac-prompt-counts">
        <UiBadge variant="outline">{{ fragments.length }} {{ t('settings.fragmentCountLabel') }}</UiBadge>
        <UiBadge variant="outline">{{ pipelines.length }} {{ t('settings.pipelineCountLabel') }}</UiBadge>
        <UiButton variant="outline" size="xs" :disabled="fragLoading || !canAccessBackend" @click="refreshAll">
          <RefreshCw :size="12" />
          <span>{{ t('settings.refresh') }}</span>
        </UiButton>
      </div>
    </div>

    <div class="ac-prompt-toolbar">
      <div class="model-provider-filters" role="group" :aria-label="t('settings.promptViewLabel')">
        <button :class="{ active: promptCanvasMode === 'fragments' }" :aria-pressed="promptCanvasMode === 'fragments'" @click="promptCanvasMode = 'fragments'">
          <FileText :size="12" />
          <span>{{ t('settings.fragmentsView') }}</span>
        </button>
        <button :class="{ active: promptCanvasMode === 'canvas' }" :aria-pressed="promptCanvasMode === 'canvas'" @click="promptCanvasMode = 'canvas'">
          <Workflow :size="12" />
          <span>{{ t('settings.pipelineView') }}</span>
        </button>
      </div>
    </div>

    <div class="pe-studio-layout">
      <!-- Left: Prompt Studio (Fragments OR Pipeline Canvas) -->
      <div class="pe-studio-main">
        <!-- Canvas 视图 -->
        <template v-if="promptCanvasMode === 'canvas'">
          <div class="ac-pipeline-picker">
            <UiLabel>{{ t('settings.pipelinePicker') }}</UiLabel>
            <UiSelectField
              v-model="pipelineCanvasSelectedId"
              class="ac-pipeline-select"
              :options="pipelines.map((p) => ({ value: p.id, label: `${pipelineDisplayName(p)} · ${p.status ? definitionStatusLabel(p.status) : t('common.draft')}` }))"
            />
            <UiButton size="sm" variant="outline" :disabled="!canAccessBackend" @click="showCreatePipeline = true">
              <Plus :size="13" />
              <span>{{ t('settings.promptStudio.newPipeline') }}</span>
            </UiButton>
            <UiButton size="sm" variant="outline" :disabled="!pipelineCanvasPipeline || pipelineSaving || !canAccessBackend" @click="cloneCurrentPipeline()">
              <Copy :size="13" />
              <span>{{ t('settings.promptStudio.clonePipeline') }}</span>
            </UiButton>
          </div>

          <div v-if="pipelineCanvasPipeline" class="pe-pipeline-meta-row">
            <div class="pe-field">
              <UiLabel>{{ t('settings.promptStudio.pipelineName') }}</UiLabel>
              <UiInput v-model="pipelineDraftName" :disabled="isPipelineReadonly" />
            </div>
            <div class="pe-field pe-field-grow">
              <UiLabel>{{ t('settings.promptStudio.pipelineDescription') }}</UiLabel>
              <UiInput v-model="pipelineDraftDesc" :disabled="isPipelineReadonly" />
            </div>
            <UiBadge v-if="isPipelineReadonly" variant="secondary">{{ t('settings.promptStudio.pipelineReadonlyHint') }}</UiBadge>
          </div>

          <PromptPipelineCanvas
            :pipeline="activePipelineForCanvas"
            :selected-node-id="selectedNodeId"
            :selected-edge-id="selectedEdgeId"
            :readonly="isPipelineReadonly"
            :dirty="pipelineDirty"
            :saving="pipelineSaving"
            :publishing="pipelinePublishing"
            @update:nodes="handlePipelineCanvasUpdateNodes"
            @update:edges="handlePipelineCanvasUpdateEdges"
            @select-node="handleSelectNode"
            @select-edge="handleSelectEdge"
            @save-draft="savePipelineDraft"
            @publish="pipelineCanvasPipeline && publishPipeline(pipelineCanvasPipeline.id)"
          />

          <!-- Selected Node / Edge Inspector -->
          <UiCard v-if="pipelineCanvasPipeline" class="pe-inspector-card" data-testid="pipeline-node-inspector">
            <template #content>
              <template v-if="selectedNode">
                <div class="pe-section-head">
                  <div class="pe-section-title">
                    <Workflow :size="14" />
                    <span>{{ t('settings.promptStudio.nodeInspector') }} · {{ selectedNode.id }}</span>
                  </div>
                  <UiBadge variant="outline">{{ nodeTypeOf(selectedNode) }}</UiBadge>
                </div>
                <div class="pe-inspector-grid">
                  <div class="pe-field">
                    <UiLabel>{{ t('settings.promptStudio.nodeLabel') }}</UiLabel>
                    <UiInput
                      :model-value="String(selectedNode.label ?? '')"
                      :disabled="isPipelineReadonly"
                      @update:model-value="patchSelectedNode((n) => ({ ...n, label: String($event) }))"
                    />
                  </div>
                  <div v-if="nodeTypeOf(selectedNode) === 'fragment_ref'" class="pe-field">
                    <UiLabel>{{ t('settings.promptStudio.fragmentRef') }}</UiLabel>
                    <UiSelectField
                      :model-value="String((selectedNode.config?.fragment_id ?? selectedNode.config?.fragment_key) ?? '')"
                      :disabled="isPipelineReadonly"
                      :options="fragments.map((f) => ({ value: f.id, label: `${f.title} (${f.key})` }))"
                      @update:model-value="(val) => {
                        const frag = fragments.find((f) => f.id === val)
                        patchSelectedNode((n) => ({ ...n, config: { ...(n.config ?? {}), fragment_id: val, fragment_key: frag?.key ?? '' } }))
                      }"
                    />
                  </div>
                  <template v-else-if="nodeTypeOf(selectedNode) === 'condition'">
                    <div class="pe-field">
                      <UiLabel>{{ t('settings.promptStudio.conditionField') }}</UiLabel>
                      <UiSelectField
                        :model-value="String(selectedNode.config?.field ?? 'layer')"
                        :disabled="isPipelineReadonly"
                        :options="[
                          { value: 'layer', label: 'layer' },
                          { value: 'mode_slug', label: 'mode_slug' },
                          { value: 'has_workspace', label: 'has_workspace' },
                          { value: 'has_tools', label: 'has_tools' },
                          { value: 'capability', label: 'capability' },
                        ]"
                        @update:model-value="patchSelectedNode((n) => ({ ...n, config: { ...(n.config ?? {}), field: String($event) } }))"
                      />
                    </div>
                    <div class="pe-field">
                      <UiLabel>{{ t('settings.promptStudio.conditionValue') }}</UiLabel>
                      <UiInput
                        :model-value="String(selectedNode.config?.value ?? '')"
                        :disabled="isPipelineReadonly"
                        @update:model-value="patchSelectedNode((n) => ({ ...n, config: { ...(n.config ?? {}), value: String($event) } }))"
                      />
                    </div>
                  </template>
                </div>
                <div v-if="nodeTypeOf(selectedNode) === 'template'" class="pe-field">
                  <UiLabel>{{ t('settings.promptStudio.templateContent') }}</UiLabel>
                  <textarea
                    :value="localizedPackTemplate(selectedNode) ?? String(selectedNode.config?.content ?? selectedNode.config?.template ?? '')"
                    :readonly="isPipelineReadonly"
                    class="pe-template-textarea"
                    rows="5"
                    @input="patchSelectedNode((n) => ({ ...n, config: { ...(n.config ?? {}), content: ($event.target as HTMLTextAreaElement).value } }))"
                  ></textarea>
                  <small class="quiet">{{ t('settings.promptStudio.templateVarsHint') }}</small>
                </div>
              </template>

              <template v-else-if="selectedEdge">
                <div class="pe-section-head">
                  <div class="pe-section-title">
                    <GitBranch :size="14" />
                    <span>{{ t('settings.promptStudio.edgeInspector') }} · {{ selectedEdge.source }} → {{ selectedEdge.target }}</span>
                  </div>
                </div>
                <div class="pe-field">
                  <UiLabel>{{ t('settings.promptStudio.edgeLabel') }}</UiLabel>
                  <UiInput
                    :model-value="selectedEdge.label ?? ''"
                    :disabled="isPipelineReadonly"
                    @update:model-value="patchSelectedEdgeLabel(String($event))"
                  />
                </div>
              </template>

              <p v-else class="quiet pe-empty-sm">{{ t('settings.promptStudio.selectNodeHint') }}</p>
            </template>
          </UiCard>

          <div class="ac-pipeline-grid">
            <UiCard v-for="p in pipelines" :key="p.id">
              <template #content>
                <div class="ac-pipeline-card-head">
                  {{ pipelineDisplayName(p) }}
                  <UiBadge variant="outline">{{ p.status ? definitionStatusLabel(p.status) : t('common.draft') }}</UiBadge>
                </div>
                <div class="quiet ac-pipeline-card-desc">{{ pipelineDescription(p) || '—' }}</div>
                <div class="ac-pipeline-card-actions">
                  <UiButton v-if="!p.pack_managed" size="xs" :disabled="pipelinePublishing" @click="publishPipeline(p.id)">{{ t('settings.publishAction') }}</UiButton>
                  <UiButton size="xs" variant="outline" @click="cloneCurrentPipeline(p.id)">{{ t('settings.promptStudio.cloneFragment') }}</UiButton>
                  <UiButton size="xs" variant="ghost" @click="pipelineCanvasSelectedId = p.id">{{ t('settings.viewOnCanvas') }}</UiButton>
                </div>
              </template>
            </UiCard>
          </div>
        </template>

        <!-- Fragments 视图 -->
        <template v-else>
          <div class="model-provider-toolbar">
            <div class="model-provider-search">
              <Search :size="15" />
              <UiInput v-model="fragQuery" :placeholder="t('settings.fragmentSearchPlaceholder')" />
            </div>
            <div class="model-provider-filters" role="group" :aria-label="t('settings.fragmentEnabledFilter')">
              <button :class="{ active: fragEnabledFilter === 'all' }" :aria-pressed="fragEnabledFilter === 'all'" @click="fragEnabledFilter = 'all'">{{ t('settings.allStatuses') }}</button>
              <button :class="{ active: fragEnabledFilter === 'enabled' }" :aria-pressed="fragEnabledFilter === 'enabled'" @click="fragEnabledFilter = 'enabled'">{{ t('settings.enabledStatus') }}</button>
              <button :class="{ active: fragEnabledFilter === 'disabled' }" :aria-pressed="fragEnabledFilter === 'disabled'" @click="fragEnabledFilter = 'disabled'">{{ t('settings.disabledStatus') }}</button>
            </div>
            <span class="model-provider-count">{{ filteredFragments.length }} / {{ fragments.length }}</span>
            <UiButton size="sm" :disabled="!canAccessBackend" @click="startNewFragment">
              <Plus :size="14" />
              <span>{{ t('settings.promptStudio.newFragment') }}</span>
            </UiButton>
          </div>

          <div class="center-workbench workbench-duo">
            <aside class="center-resource-rail pe-fragment-list">
              <div class="pe-list-header">
                <h3>{{ t('settings.fragmentsView') }}</h3>
                <UiBadge variant="outline">{{ filteredFragments.length }}</UiBadge>
              </div>
              <div v-if="filteredFragments.length === 0" class="quiet pe-empty">{{ t('agentCenter.empty.noPrompts') }}</div>
              <button
                v-for="fragment in filteredFragments"
                :key="fragment.id"
                class="pe-fragment-card"
                :class="{ active: !creatingFragment && selectedFragmentId === fragment.id }"
                @click="selectFragment(fragment)"
              >
                <div class="pe-fragment-head">
                  <strong :title="fragment.title">{{ fragment.title }}</strong>
                  <UiBadge v-if="fragment.is_builtin" variant="secondary">{{ t('settings.builtinBadge') }}</UiBadge>
                </div>
                <span class="pe-fragment-meta">{{ fragment.scope }} / {{ fragment.category }} · {{ t('settings.fragmentPriority', { priority: fragment.priority }) }}</span>
              </button>
            </aside>

            <main class="center-resource-stage pe-detail">
              <template v-if="selectedFragment || creatingFragment">
                <UiCard class="pe-detail-card">
                  <template #content>
                    <div class="pe-detail-head">
                      <div>
                        <h3>{{ creatingFragment ? t('settings.promptStudio.newFragment') : selectedFragment!.title }}</h3>
                        <p v-if="selectedFragment">{{ selectedFragment.key }} · {{ selectedFragment.scope }} / {{ selectedFragment.category }}</p>
                      </div>
                      <div class="pe-detail-head-actions">
                        <label class="pe-switch-inline">
                          <span>{{ t('settings.promptStudio.fragmentEnabledLabel') }}</span>
                          <UiSwitch
                            :model-value="fragmentDraft.enabled"
                            :disabled="isReadonlyFragment || fragBusy"
                            @update:model-value="toggleFragmentEnabled($event)"
                          />
                        </label>
                        <UiButton v-if="selectedFragment" size="xs" variant="outline" :disabled="fragBusy || !canAccessBackend" @click="cloneCurrentFragment">
                          <Copy :size="12" />
                          <span>{{ t('settings.promptStudio.cloneFragment') }}</span>
                        </UiButton>
                        <UiButton v-if="selectedFragment && !isReadonlyFragment" size="xs" variant="ghost" :disabled="fragBusy || !canAccessBackend" @click="deleteCurrentFragment">
                          <Trash2 :size="12" />
                          <span>{{ t('settings.promptStudio.deleteFragment') }}</span>
                        </UiButton>
                      </div>
                    </div>

                    <p v-if="isReadonlyFragment" class="pe-readonly-banner">{{ t('settings.promptStudio.builtinReadonlyHint') }}</p>

                    <div v-if="selectedFragment && fragmentEffectiveness" class="pe-metrics">
                      <div class="pe-metric"><Activity :size="14" /><div><span>{{ t('settings.fragmentScore') }}</span><strong>{{ (fragmentEffectiveness.effectiveness_score * 100).toFixed(0) }}%</strong></div></div>
                      <div class="pe-metric"><History :size="14" /><div><span>v</span><strong>{{ fragmentEffectiveness.active_version }}</strong></div></div>
                      <div class="pe-metric"><ThumbsUp :size="14" /><div><span>+</span><strong>{{ fragmentEffectiveness.positive_signals }}</strong></div></div>
                      <div class="pe-metric"><ThumbsDown :size="14" /><div><span>-</span><strong>{{ fragmentEffectiveness.negative_signals }}</strong></div></div>
                    </div>

                    <div class="pe-form-grid">
                      <div class="pe-field">
                        <UiLabel>{{ t('settings.promptStudio.fragmentTitle') }}</UiLabel>
                        <UiInput v-model="fragmentDraft.title" :disabled="isReadonlyFragment" />
                      </div>
                      <div class="pe-field">
                        <UiLabel>{{ t('settings.promptStudio.fragmentKey') }}</UiLabel>
                        <UiInput v-model="fragmentDraft.key" :disabled="isReadonlyFragment" />
                      </div>
                      <div class="pe-field">
                        <UiLabel>{{ t('settings.promptStudio.fragmentScope') }}</UiLabel>
                        <UiSelectField
                          v-model="fragmentDraft.scope"
                          :disabled="isReadonlyFragment"
                          :options="[
                            { value: 'global', label: t('settings.promptStudio.scopeGlobal') },
                            { value: 'mode', label: t('settings.promptStudio.scopeMode') },
                            { value: 'agent', label: t('settings.promptStudio.scopeAgent') },
                          ]"
                        />
                      </div>
                      <div v-if="fragmentDraft.scope === 'agent'" class="pe-field">
                        <UiLabel>{{ t('settings.promptStudio.fragmentTargetAgent') }}</UiLabel>
                        <UiSelectField
                          v-if="agents.length > 0"
                          v-model="fragmentDraft.target_agent_id"
                          :disabled="isReadonlyFragment"
                          :options="agents.map((a) => ({ value: a.id, label: `${agentDisplayName(a)} (${a.slug || a.id.slice(0, 8)})` }))"
                        />
                        <UiInput v-else v-model="fragmentDraft.target_agent_id" :disabled="isReadonlyFragment" placeholder="meeting" />
                      </div>
                      <div class="pe-field">
                        <UiLabel>{{ t('settings.promptStudio.fragmentCategory') }}</UiLabel>
                        <UiInput v-model="fragmentDraft.category" :disabled="isReadonlyFragment" placeholder="system / mode_profile / agent_profile / task" />
                      </div>
                      <div class="pe-field">
                        <UiLabel>{{ t('settings.promptStudio.fragmentPriorityLabel') }}</UiLabel>
                        <UiInput
                          :model-value="String(fragmentDraft.priority)"
                          type="number"
                          :disabled="isReadonlyFragment"
                          @update:model-value="fragmentDraft.priority = Number($event) || 0"
                        />
                      </div>
                    </div>

                    <div class="pe-section">
                      <div class="pe-section-head">
                        <div class="pe-section-title"><GitBranch :size="14" /><span>{{ t('settings.currentContent') }}</span></div>
                        <UiButton v-if="!isReadonlyFragment" size="sm" :disabled="fragBusy || !fragmentDraft.key.trim() || !canAccessBackend" @click="saveFragmentForm">
                          <Save :size="13" />
                          <span>{{ t('settings.promptStudio.saveFragment') }}</span>
                        </UiButton>
                      </div>
                      <textarea
                        v-model="fragmentDraft.content"
                        class="pe-content-textarea"
                        rows="7"
                        :readonly="isReadonlyFragment"
                      ></textarea>
                    </div>
                  </template>
                </UiCard>

                <UiCard v-if="selectedFragment && !creatingFragment" class="pe-detail-card">
                  <template #content>
                    <div class="pe-section-head">
                      <div class="pe-section-title"><History :size="14" /><span>{{ t('settings.versionHistoryTitle') }}</span></div>
                      <UiBadge variant="outline">{{ fragmentVersions.length }}</UiBadge>
                    </div>
                    <div v-if="fragmentVersions.length === 0" class="quiet pe-empty-sm">{{ t('settings.noFragmentVersions') }}</div>
                    <div v-else class="pe-version-list">
                      <div v-for="version in sortedVersions" :key="version.id" class="center-list-row pe-version-row" :class="{ active: version.is_active }">
                        <div class="pe-version-main">
                          <div class="pe-version-head-row">
                            <strong>v{{ version.version }}</strong>
                            <UiBadge v-if="version.is_active" variant="default">{{ t('settings.activeVersion') }}</UiBadge>
                            <span class="pe-version-date">{{ new Date(version.created_at).toLocaleString(locale) }}</span>
                          </div>
                          <p class="pe-version-summary">{{ version.change_summary }}</p>
                        </div>
                        <div class="pe-version-actions">
                          <UiButton v-if="!version.is_active && !isReadonlyFragment" size="sm" variant="ghost" :disabled="fragBusy" @click="rollbackVersion(version.version)">
                            <Undo2 :size="14" /><span>{{ t('settings.rollbackAction') }}</span>
                          </UiButton>
                        </div>
                      </div>
                    </div>
                  </template>
                </UiCard>
              </template>
              <div v-else class="pe-empty-stage">
                <FileText :size="22" />
                <p>{{ t('settings.promptStudio.selectFragmentHint') }}</p>
                <UiButton size="sm" :disabled="!canAccessBackend" @click="startNewFragment">
                  <Plus :size="14" />
                  <span>{{ t('settings.promptStudio.newFragment') }}</span>
                </UiButton>
              </div>
            </main>
          </div>
        </template>
      </div>

      <!-- Right: Live Context Preview & Per-Layer Token Breakdown -->
      <aside class="pe-preview-aside" data-testid="prompt-context-preview-panel">
        <UiCard class="pe-preview-card">
          <template #content>
            <div class="pe-section-head">
              <div class="pe-section-title"><Eye :size="14" /><span>{{ t('settings.contextPreviewTitle') }}</span></div>
              <UiBadge v-if="promptPreview" variant="outline">{{ t('settings.estimatedTokens', { n: promptPreview.total_estimated_tokens }) }}</UiBadge>
            </div>

            <div class="ac-preview-fields">
              <div class="pe-field">
                <UiLabel>{{ t('settings.agentIdField') }}</UiLabel>
                <UiSelectField
                  v-if="previewAgentOptions.length > 0"
                  v-model="promptPreviewAgentId"
                  :options="previewAgentOptions"
                />
                <UiInput v-else v-model="promptPreviewAgentId" placeholder="meeting" />
              </div>
              <div class="pe-field">
                <UiLabel>{{ t('settings.modeFieldOptional') }}</UiLabel>
                <UiSelectField
                  v-model="promptPreviewModeId"
                  :options="previewModeOptions"
                />
              </div>
              <div class="pe-field">
                <UiLabel>{{ t('settings.promptStudio.previewPipelineOptional') }}</UiLabel>
                <UiSelectField
                  v-model="promptPreviewPipelineId"
                  :options="previewPipelineOptions"
                />
              </div>
              <div class="pe-field">
                <UiLabel>{{ t('settings.sessionIdOptional') }}</UiLabel>
                <UiInput v-model="promptPreviewSessionId" placeholder="session_id" />
              </div>
            </div>

            <div class="pe-preview-actions">
              <label class="pe-switch-inline">
                <input v-model="includeDraftInPreview" type="checkbox" />
                <span>{{ t('settings.promptStudio.previewDraftContent') }}</span>
              </label>
              <UiButton size="sm" :disabled="previewBusy || !canAccessBackend" @click="generatePromptPreview">
                <Eye :size="14" />
                <span>{{ t('settings.generatePreview') }}</span>
              </UiButton>
            </div>

            <template v-if="promptPreview">
              <div v-if="(promptPreview.warnings ?? []).length > 0" class="pe-preview-warnings">
                <strong>{{ t('settings.promptStudio.previewWarnings') }}</strong>
                <ul>
                  <li v-for="(w, idx) in promptPreview.warnings" :key="idx">{{ w }}</li>
                </ul>
              </div>

              <div v-if="previewLayers.length > 0" class="pe-layers-section">
                <div class="pe-section-head">
                  <div class="pe-section-title"><Layers :size="14" /><span>{{ t('settings.promptStudio.previewLayersTitle') }}</span></div>
                  <UiBadge variant="secondary">{{ previewLayers.length }}</UiBadge>
                </div>
                <div class="pe-token-bar" aria-hidden="true">
                  <div
                    v-for="layer in previewLayers"
                    :key="`bar-${layer.id}`"
                    class="pe-token-bar-seg"
                    :style="{ width: `${layerPercent(layer.estimated_tokens)}%` }"
                    :title="`${layer.title}: ${layer.estimated_tokens} tokens`"
                  />
                </div>
                <div class="pe-layer-list">
                  <div v-for="layer in previewLayers" :key="layer.id" class="pe-layer-row">
                    <button type="button" class="pe-layer-summary" @click="toggleLayerExpanded(layer.id)">
                      <span class="pe-layer-kind">{{ layer.kind }}</span>
                      <strong :title="layer.title">{{ layer.title }}</strong>
                      <span class="pe-layer-source">{{ layer.source }}</span>
                      <span class="pe-layer-tokens">{{ layer.estimated_tokens }}t</span>
                    </button>
                    <pre v-if="expandedLayers[layer.id]" class="pe-layer-content">{{ layer.content }}</pre>
                  </div>
                </div>
              </div>

              <div class="pe-section">
                <div class="pe-section-head">
                  <div class="pe-section-title"><span>{{ t('settings.promptStudio.previewAssembledTitle') }}</span></div>
                  <UiButton size="xs" variant="ghost" @click="copyAssembledPrompt">
                    <Check v-if="promptCopied" :size="12" />
                    <Copy v-else :size="12" />
                    <span>{{ promptCopied ? t('common.copied') : t('settings.promptStudio.previewCopyPrompt') }}</span>
                  </UiButton>
                </div>
                <textarea :value="promptPreview.assembled_prompt" class="pe-preview-textarea" rows="10" readonly></textarea>
              </div>
            </template>

            <p v-else class="quiet pe-preview-empty">{{ t('settings.promptStudio.previewEmptyHint') }}</p>
          </template>
        </UiCard>
      </aside>
    </div>

    <UiSheet :open="showCreatePipeline" side="right" @update:open="showCreatePipeline = $event">
      <div class="ac-sheet-body">
        <h3>{{ t('settings.promptStudio.newPipeline') }}</h3>
        <div class="pe-field">
          <UiLabel>{{ t('settings.promptStudio.pipelineName') }}</UiLabel>
          <UiInput v-model="newPipelineForm.display_name" placeholder="Custom prompt pipeline" />
        </div>
        <div class="pe-field">
          <UiLabel>{{ t('settings.promptStudio.pipelineSlug') }}</UiLabel>
          <UiInput v-model="newPipelineForm.slug" placeholder="optional" />
        </div>
        <div class="pe-field">
          <UiLabel>{{ t('settings.promptStudio.pipelineDescription') }}</UiLabel>
          <UiInput v-model="newPipelineForm.description" />
        </div>
        <div class="ac-sheet-actions">
          <UiButton size="sm" variant="outline" @click="showCreatePipeline = false">{{ t('common.cancel') }}</UiButton>
          <UiButton size="sm" :disabled="pipelineSaving || !newPipelineForm.display_name.trim()" @click="createPipelineDraft">
            {{ t('settings.promptStudio.createConfirm') }}
          </UiButton>
        </div>
      </div>
    </UiSheet>
  </section>
</template>

<style scoped>
.agent-prompts-panel {
  display: grid;
  gap: 12px;
}
.ac-prompt-counts {
  display: flex;
  align-items: center;
  gap: 6px;
}
.ac-prompt-toolbar {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  align-items: center;
}
.pe-studio-layout {
  display: grid;
  grid-template-columns: minmax(0, 1.45fr) minmax(320px, 0.95fr);
  gap: 14px;
  align-items: start;
}
@media (max-width: 1120px) {
  .pe-studio-layout {
    grid-template-columns: 1fr;
  }
}
.pe-studio-main {
  display: grid;
  gap: 10px;
  min-width: 0;
}
.pe-preview-aside {
  min-width: 0;
  position: sticky;
  top: 12px;
}
.ac-pipeline-picker {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
}
.ac-pipeline-select {
  min-width: 220px;
  flex: 1 1 220px;
}
.pe-pipeline-meta-row {
  display: flex;
  gap: 10px;
  align-items: flex-end;
  flex-wrap: wrap;
  padding: 10px 12px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}
.pe-field {
  display: grid;
  gap: 4px;
  min-width: 0;
}
.pe-field-grow {
  flex: 1 1 220px;
}
.pe-inspector-card :deep(.ui-card-content) {
  display: grid;
  gap: 10px;
}
.pe-inspector-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 10px;
}
.ac-pipeline-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
  gap: 10px;
}
.ac-pipeline-card-head {
  font-weight: 600;
  display: flex;
  gap: 6px;
  align-items: center;
  flex-wrap: wrap;
}
.ac-pipeline-card-desc {
  font-size: 12px;
  margin: 4px 0;
}
.ac-pipeline-card-actions {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  margin-top: 8px;
}
.ac-preview-fields {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
  margin-bottom: 10px;
}
@media (max-width: 600px) {
  .ac-preview-fields {
    grid-template-columns: 1fr;
  }
}
.pe-preview-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
  margin-bottom: 10px;
}
.pe-switch-inline {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-secondary);
  white-space: nowrap;
  flex-shrink: 0;
}
.pe-preview-card :deep(.ui-card-content) {
  display: grid;
  gap: 10px;
}
.pe-preview-warnings {
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px solid color-mix(in srgb, var(--accent-warning) 35%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-warning) 8%, var(--surface-raised));
  font-size: 11px;
}
.pe-preview-warnings ul {
  margin: 4px 0 0;
  padding-left: 16px;
}
.pe-layers-section {
  display: grid;
  gap: 8px;
}
.pe-token-bar {
  display: flex;
  height: 8px;
  border-radius: 999px;
  overflow: hidden;
  gap: 2px;
  background: var(--surface-chrome);
}
.pe-token-bar-seg {
  height: 100%;
  background: var(--accent-primary);
  opacity: 0.78;
}
.pe-token-bar-seg:nth-child(2n) {
  background: var(--accent-brand);
}
.pe-token-bar-seg:nth-child(3n) {
  background: var(--accent-success);
}
.pe-layer-list {
  display: grid;
  gap: 4px;
  max-height: 240px;
  overflow-y: auto;
}
.pe-layer-row {
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--surface-chrome);
  overflow: hidden;
}
.pe-layer-summary {
  width: 100%;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  background: transparent;
  border: 0;
  text-align: left;
  font-size: 11px;
  cursor: pointer;
  color: var(--text-primary);
}
.pe-layer-summary strong {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.pe-layer-kind {
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 10px;
  font-family: var(--font-mono);
}
.pe-layer-source {
  color: var(--text-muted);
  font-size: 10px;
  max-width: 110px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.pe-layer-tokens {
  font-family: var(--font-mono);
  font-weight: 600;
  color: var(--accent-primary);
}
.pe-layer-content {
  margin: 0;
  padding: 8px;
  border-top: 1px solid var(--border-muted);
  font-family: var(--font-mono);
  font-size: 11px;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 160px;
  overflow-y: auto;
  background: var(--surface-raised);
}
.pe-fragment-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.pe-list-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 4px;
}
.pe-list-header h3 {
  font-size: 13px;
}
.pe-fragment-card {
  display: grid;
  gap: 4px;
  text-align: left;
  padding: 10px 12px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-chrome);
  cursor: pointer;
}
.pe-fragment-card.active {
  border-color: var(--accent-brand);
  background: var(--surface-raised);
}
.pe-fragment-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
}
.pe-fragment-meta {
  font-size: 11px;
  color: var(--text-secondary);
}
.pe-detail {
  display: grid;
  gap: 12px;
  min-width: 0;
}
.pe-detail-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}
.pe-detail-head-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.pe-readonly-banner {
  margin: 6px 0;
  padding: 6px 10px;
  border-radius: 6px;
  background: var(--surface-chrome);
  color: var(--text-secondary);
  font-size: 12px;
}
.pe-form-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 8px;
  margin: 8px 0;
}
.pe-metrics {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(110px, 1fr));
  gap: 8px;
  margin: 10px 0;
}
.pe-metric {
  display: flex;
  gap: 6px;
  align-items: center;
  padding: 8px;
  border-radius: 8px;
  background: var(--surface-chrome);
}
.pe-section {
  display: grid;
  gap: 8px;
}
.pe-section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}
.pe-section-title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  font-weight: 600;
}
.pe-content-textarea,
.pe-template-textarea,
.pe-preview-textarea {
  width: 100%;
  font-family: var(--font-mono);
  font-size: 12px;
  padding: 8px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-chrome);
  color: var(--text-primary);
  resize: vertical;
}
.pe-version-list {
  display: grid;
  gap: 6px;
  max-height: 260px;
  overflow-y: auto;
}
.pe-version-head-row {
  display: flex;
  gap: 6px;
  align-items: center;
}
.pe-version-date {
  margin-left: auto;
  color: var(--text-muted);
  font-size: 11px;
}
.pe-version-summary {
  font-size: 12px;
  color: var(--text-secondary);
}
.pe-empty-stage {
  display: grid;
  place-items: center;
  gap: 10px;
  min-height: 220px;
  padding: 24px;
  border-radius: 10px;
  border: 1px dashed var(--border-muted);
  background: var(--surface-chrome);
  text-align: center;
  color: var(--text-muted);
  font-size: 13px;
}
.pe-empty,
.pe-preview-empty {
  font-size: 12px;
  padding: 10px 4px;
}
.pe-empty-sm {
  font-size: 12px;
}
.ac-sheet-body {
  display: grid;
  gap: 12px;
  min-width: 300px;
  padding: 4px 0;
}
.ac-sheet-actions {
  display: flex;
  gap: 8px;
  justify-content: flex-end;
  margin-top: 10px;
}
.quiet {
  color: var(--text-muted);
}
</style>
