<script setup lang="ts">
import { ref, onMounted, computed, watch } from 'vue'
import {
  api,
  type AgentDefinitionDto,
  type AgentModeTopologyDto,
  type PromptPipelineDto,
  type AgentCandidateDto,
  type AgentRuntimeInstanceDto,
  type AgentModeNodeDto,
  type AgentModeEdgeDto,
  type HarnessManifestDto,
  type AgentEvolutionProposalDto,
  type PromoteAgentCandidateInput,
  type PromptFragmentDto,
  type PromptFragmentVersionDto,
  type PromptFragmentEffectivenessDto,
  type PromptFragmentAbTestResultDto,
  type PromptContextPreviewDto
} from '@/api'
import { generatedApi } from '@/generated/client'
import { useNotifications } from '@/composables/useNotifications'
import { useI18n } from 'vue-i18n'
import { UiButton, UiCard, UiBadge, UiInput, UiLabel, UiSwitch, UiTextarea, UiSkeleton, UiCollapsible, UiSheet } from '@/components/ui'
import AgentModeCanvas from '@/components/canvas/AgentModeCanvas.vue'
import PromptPipelineCanvas from '@/components/canvas/PromptPipelineCanvas.vue'
import { manifestTools } from '@/toolCatalog'
import {
  Bot,
  Workflow,
  FileText,
  Dna,
  Info,
  Search,
  RefreshCw,
  Plus,
  ChevronRight,
  Settings2,
  Cpu,
  Trash2,
  History,
  Activity,
  ArrowLeftRight,
  GitBranch,
  ThumbsUp,
  ThumbsDown,
  Undo2,
  Eye,
  Check,
  X,
  Layers,
  Sparkles
} from '@lucide/vue'
import '../settings/settings.css'

const { t } = useI18n()
const { notify, status, confirm, dismissByKey } = useNotifications()

// ── Rail ──
type SectionKey = 'config' | 'topology' | 'prompt' | 'evolution' | 'info'
const activeSection = ref<SectionKey>('config')

// ── Shared formatting ──
function normalizeLayer(v: unknown): 'operation' | 'execution' {
  const s = String(v ?? '').trim().toLowerCase()
  if (s === 'planning') return 'operation'
  return s === 'execution' ? 'execution' : (s as 'operation' | 'execution')
}
function agentTypeLabel(t: string) { return t || '—' }
function readinessVariant(status?: string | null): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (status === 'published' || status === 'ready' || status === 'active') return 'default'
  if (status === 'archived' || status === 'disabled') return 'outline'
  if (status === 'blocked' || status === 'rejected' || status === 'failed') return 'destructive'
  return 'secondary'
}
function formatDate(iso: string) {
  try { return new Date(iso).toLocaleString() } catch { return iso }
}

// ── Section 1: Agents (Definitions) ──
const agents = ref<AgentDefinitionDto[]>([])
const agentsLoading = ref(false)
const agentQuery = ref('')
const agentLayerFilter = ref<'all' | 'operation' | 'execution'>('all')
const agentStatusFilter = ref<'all' | 'draft' | 'published' | 'archived'>('all')
const expandedAgentId = ref<string | null>(null)
const agentVersions = ref<Record<string, unknown>[]>([])
const agentDraft = ref<Partial<AgentDefinitionDto> & { etag?: string | null }>({
  name: '',
  layer: 'operation',
  agent_type: 'assistant',
  description: '',
  allowed_tools: [],
  capabilities: [],
  system_prompt: '',
  enabled: true
})
const editingAgentId = ref<string | null>(null)
const showAgentForm = ref(false)
const agentCapInput = ref('')
const agentToolInput = ref('')

function addCap() {
  const v = agentCapInput.value.trim()
  if (!v) return
  const list = agentDraft.value.capabilities ?? []
  if (!list.includes(v)) list.push(v)
  agentDraft.value.capabilities = [...list]
  agentCapInput.value = ''
}
function removeCap(i: number) {
  agentDraft.value.capabilities = (agentDraft.value.capabilities ?? []).filter((_, idx) => idx !== i)
}
function addTool() {
  const v = agentToolInput.value.trim()
  if (!v) return
  const list = agentDraft.value.allowed_tools ?? [];
  if (!list.includes(v)) list.push(v)
  agentDraft.value.allowed_tools = [...list]
  agentToolInput.value = ''
}
function removeTool(i: number) {
  agentDraft.value.allowed_tools = (agentDraft.value.allowed_tools ?? []).filter((_, idx) => idx !== i)
}
function toggleManifestTool(id: string) {
  const list = new Set(agentDraft.value.allowed_tools ?? [])
  if (list.has(id)) list.delete(id); else list.add(id)
  agentDraft.value.allowed_tools = [...list]
}

const harnessManifest = ref<HarnessManifestDto | null>(null)
const manifestToolList = computed(() => manifestTools(harnessManifest.value, []))

const filteredAgents = computed(() => {
  const q = agentQuery.value.trim().toLowerCase()
  return agents.value.filter((a) => {
    if (agentLayerFilter.value !== 'all' && normalizeLayer(a.layer) !== agentLayerFilter.value) return false
    const st = (a.status ?? 'draft') as string
    if (agentStatusFilter.value !== 'all' && st !== agentStatusFilter.value) return false
    if (!q) return true
    return [a.name, a.agent_type, a.description ?? ''].some((v) => String(v).toLowerCase().includes(q))
  })
})

const publishedCount = computed(() => agents.value.filter((a) => (a.status ?? 'draft') === 'published').length)

async function loadAgents() {
  agentsLoading.value = true
  try {
    const list = await api.listAgentDefinitions()
    agents.value = Array.isArray(list) ? list : (list as unknown as { data: AgentDefinitionDto[] })?.data ?? []
    if (!expandedAgentId.value && agents.value[0]) {
      expandedAgentId.value = agents.value[0].id
    }
  } catch (e) {
    notify.error(e, { title: '加载智能体定义失败' })
  } finally {
    agentsLoading.value = false
  }
}
async function loadManifest() {
  try { harnessManifest.value = await api.getHarnessManifest() } catch { /* optional */ }
}
function openCreateAgent() {
  editingAgentId.value = null
  agentDraft.value = {
    name: '',
    layer: 'operation',
    agent_type: 'assistant',
    description: '',
    allowed_tools: [],
    capabilities: [],
    system_prompt: '',
    enabled: true,
    etag: null
  }
  showAgentForm.value = true
}
function openEditAgent(a: AgentDefinitionDto) {
  editingAgentId.value = a.id
  agentDraft.value = {
    ...a,
    allowed_tools: [...(a.allowed_tools ?? [])],
    capabilities: [...(a.capabilities ?? [])],
    etag: (a as unknown as { etag?: string }).etag ?? (a.revision != null ? String(a.revision) : null)
  }
  showAgentForm.value = true
}
async function saveAgentDraft() {
  try {
    const body: Partial<AgentDefinitionDto> = {
      name: agentDraft.value.name,
      layer: agentDraft.value.layer,
      agent_type: agentDraft.value.agent_type,
      description: agentDraft.value.description,
      allowed_tools: agentDraft.value.allowed_tools,
      capabilities: agentDraft.value.capabilities,
      system_prompt: agentDraft.value.system_prompt,
      enabled: agentDraft.value.enabled,
    }
    if (editingAgentId.value) {
      const updated = await api.updateAgentDraft(editingAgentId.value, body, agentDraft.value.etag ?? null)
      notify.success({ title: t('agentCenter.agentForm.saveSuccess'), message: updated.name })
    } else {
      const created = await api.createAgentDraft(body)
      notify.success({ title: t('agentCenter.agentForm.saveSuccess'), message: created.name })
    }
    showAgentForm.value = false
    await loadAgents()
  } catch (e) {
    notify.error(e, { title: '保存草稿失败' })
  }
}
async function publishAgent(id: string) {
  try {
    await api.publishAgent(id)
    notify.success({ title: t('agentCenter.agentForm.publishSuccess'), message: id })
    await loadAgents()
  } catch (e) {
    notify.error(e)
  }
}
async function archiveAgent(id: string) {
  try {
    await api.archiveAgent(id)
    notify.success({ message: '智能体已归档' })
    await loadAgents()
  } catch (e) {
    notify.error(e)
  }
}
async function fetchAgentVersions(id: string) {
  try {
    agentVersions.value = (await api.listAgentVersions(id)) as unknown as Record<string, unknown>[]
  } catch (e) {
    notify.error(e)
  }
}
function selectAgentItem(id: string) {
  expandedAgentId.value = id
  fetchAgentVersions(id)
}

// ── Section 2: Modes (Topology) ──
const modes = ref<AgentModeTopologyDto[]>([])
const selectedModeId = ref<string | null>(null)
const modeNodes = ref<AgentModeNodeDto[]>([])
const modeEdges = ref<AgentModeEdgeDto[]>([])
const modeEtag = ref<string | null>(null)
const selectedModeNode = ref<AgentModeNodeDto | null>(null)
const selectedEdge = ref<AgentModeEdgeDto | null>(null)
const edgeLabelDraft = ref('')
const modeQuery = ref('')
const showNodeSheet = ref(false)
const showVersionDrawer = ref(false)
const modeVersions = ref<{ id: string; version: number; created_at: string; is_active?: boolean }[]>([])
const modeVersionsLoading = ref(false)
const nodeOverride = ref<{ agent_id: string; label: string }>({ agent_id: '', label: '' })

const selectedMode = computed(() => modes.value.find((m) => m.id === selectedModeId.value) ?? null)
const filteredModes = computed(() => {
  const q = modeQuery.value.trim().toLowerCase()
  if (!q) return modes.value
  return modes.value.filter((m) => [m.display_name, m.id, m.status ?? ''].some((v) => String(v).toLowerCase().includes(q)))
})

const unboundNodeCount = computed(() => modeNodes.value.filter((n) => !agents.value.some((a) => a.id === n.agent_id)).length)
const isolatedNodeIds = computed(() => {
  const linked = new Set<string>()
  for (const e of modeEdges.value) { linked.add(e.source); linked.add(e.target) }
  return modeNodes.value.filter((n) => !linked.has(n.id)).map((n) => n.id)
})
const healthWarnings = computed(() => {
  const warns: string[] = []
  if (isolatedNodeIds.value.length > 0) warns.push(`孤岛节点 ${isolatedNodeIds.value.length} 个`)
  if (unboundNodeCount.value > 0) warns.push(`未绑定智能体 ${unboundNodeCount.value} 个`)
  if (modeNodes.value.length === 0) warns.push('空生效节点')
  return warns
})

async function loadModes() {
  try {
    const list = await api.listAgentModeTopologies()
    modes.value = Array.isArray(list) ? list : []
    if (!selectedModeId.value && modes.value[0]) selectMode(modes.value[0].id)
  } catch (e) {
    notify.error(e, { title: '加载运行拓扑失败' })
  }
}
async function selectMode(id: string) {
  selectedModeId.value = id
  try {
    const m = await api.getAgentModeTopology(id)
    modeNodes.value = (m.nodes ?? []) as AgentModeNodeDto[]
    modeEdges.value = (m.edges ?? []) as AgentModeEdgeDto[]
    modeEtag.value = (m as unknown as { etag?: string }).etag ?? (m.revision != null ? String(m.revision) : null)
  } catch {
    modeNodes.value = []
    modeEdges.value = []
    modeEtag.value = null
  }
}
async function createMode() {
  try {
    const m = await api.createAgentModeDraft({
      display_name: `mode-${Date.now().toString(36).slice(0, 6)}`,
      summary: 'draft',
      nodes: [],
      edges: [],
      canvas_layout: {}
    })
    await loadModes()
    selectedModeId.value = m.id
    notify.success({ title: '已创建模式草稿', message: m.display_name })
  } catch (e) {
    notify.error(e)
  }
}
async function saveModeDraft() {
  if (!selectedModeId.value) return
  try {
    await api.updateAgentModeDraft(selectedModeId.value, { nodes: modeNodes.value, edges: modeEdges.value, canvas_layout: {} }, modeEtag.value)
    notify.success({ title: '模式草稿已保存', message: '需重新发布后生效' })
    await loadModes()
  } catch (e) {
    notify.error(e, { title: '保存模式失败' })
  }
}
async function publishMode() {
  if (!selectedModeId.value) return
  try {
    await api.publishAgentMode(selectedModeId.value)
    notify.success({ message: '模式已成功发布' })
    await loadModes()
  } catch (e) {
    notify.error(e)
  }
}
function addModeNode() {
  const id = `n-${Date.now().toString(36)}`
  const firstAgent = agents.value[0]?.id ?? 'agent_meeting'
  modeNodes.value = [
    ...modeNodes.value,
    { id, agent_id: firstAgent, lane: 'operation', position: { x: 80 + modeNodes.value.length * 40, y: 80 }, label: `node ${modeNodes.value.length + 1}` }
  ]
}
function handleSelectNode(n: AgentModeNodeDto | null) {
  selectedModeNode.value = n
  selectedEdge.value = null
  if (n) {
    nodeOverride.value = { agent_id: n.agent_id, label: String(n.label ?? '') }
    showNodeSheet.value = true
  }
}
function handleSelectEdge(e: AgentModeEdgeDto | null) {
  selectedEdge.value = e
  selectedModeNode.value = null
  if (e) edgeLabelDraft.value = String(e.label ?? '')
}
function applyNodeOverride() {
  if (!selectedModeNode.value) return
  modeNodes.value = modeNodes.value.map((n) =>
    n.id === selectedModeNode.value!.id ? { ...n, agent_id: nodeOverride.value.agent_id, label: nodeOverride.value.label } : n
  )
  notify.success({ message: '节点属性已更新' })
  showNodeSheet.value = false
}
function deleteEdge() {
  if (!selectedEdge.value) return
  modeEdges.value = modeEdges.value.filter((x) => x.id !== selectedEdge.value!.id)
  selectedEdge.value = null
}
function saveEdgeLabel() {
  if (!selectedEdge.value) return
  const v = edgeLabelDraft.value.trim()
  modeEdges.value = modeEdges.value.map((x) => (x.id === selectedEdge.value!.id ? { ...x, label: v || null } : x))
  selectedEdge.value = { ...selectedEdge.value, label: v || null }
  notify.success({ message: '边标签已更新' })
}
async function loadModeVersions() {
  if (!selectedModeId.value) return
  modeVersionsLoading.value = true
  try {
    const list = await api.listAgentModeVersions(selectedModeId.value)
    modeVersions.value = (Array.isArray(list) ? list : []) as typeof modeVersions.value
  } catch (e) {
    notify.error(e, { title: '加载版本失败' })
  } finally {
    modeVersionsLoading.value = false
  }
}
function openVersionDrawer() {
  showVersionDrawer.value = true
  loadModeVersions()
}

// ── Section 3: Prompt Engine ──
const fragments = ref<PromptFragmentDto[]>([])
const effectivenessList = ref<PromptFragmentEffectivenessDto[]>([])
const selectedFragmentId = ref('')
const fragmentVersions = ref<PromptFragmentVersionDto[]>([])
const fragmentEffectiveness = ref<PromptFragmentEffectivenessDto | null>(null)
const fragLoading = ref(false)
const fragBusy = ref(false)
const fragQuery = ref('')
const fragScopeFilter = ref<string>('all')
const fragCategoryFilter = ref<string>('all')
const fragTargetFilter = ref<string>('all')
const fragEnabledFilter = ref<'all' | 'enabled' | 'disabled'>('all')
const showNewVersion = ref(false)
const newVersionContent = ref('')
const newVersionChangedFields = ref('content')
const newVersionSummary = ref('')
const signalNote = ref('')
const signalVersion = ref<number | null>(null)
const compareVersionA = ref<number | null>(null)
const compareVersionB = ref<number | null>(null)
const compareResult = ref<PromptFragmentAbTestResultDto | null>(null)
const promptCanvasMode = ref<'fragments' | 'canvas'>('fragments')

// Pipelines for canvas
const pipelines = ref<PromptPipelineDto[]>([])
const pipelineDraft = ref<Partial<PromptPipelineDto>>({ name: '', description: '', scope: 'global' })
const editingPipelineId = ref<string | null>(null)
const showPipelineForm = ref(false)
const pipelineCanvasSelectedId = ref<string | null>(null)
const pipelineCanvasNodes = ref<unknown[]>([])
const pipelineCanvasEdges = ref<unknown[]>([])
const pipelineCanvasSelectedNode = ref<unknown | null>(null)
const pipelineCanvasSelectedEdge = ref<unknown | null>(null)

const promptPreviewAgentId = ref('agent_meeting')
const promptPreviewMode = ref('')
const promptPreviewSessionId = ref('')
const promptPreviewRunId = ref('')
const promptPreviewUserContent = ref('')
const promptPreview = ref<PromptContextPreviewDto | null>(null)
const previewBusy = ref(false)

const selectedFragment = computed(() => fragments.value.find((f) => f.id === selectedFragmentId.value) ?? null)
const selectedEffectiveness = computed(() => effectivenessList.value.find((e) => e.fragment_id === selectedFragmentId.value) ?? null)
const sortedVersions = computed(() => [...fragmentVersions.value].sort((a, b) => b.version - a.version))
const sortedEffectivenessList = computed(() => [...effectivenessList.value].sort((a, b) => b.effectiveness_score - a.effectiveness_score))
const scopeOptions = computed(() => ['all', ...Array.from(new Set(fragments.value.map((f) => f.scope).filter(Boolean)))])
const categoryOptions = computed(() => ['all', ...Array.from(new Set(fragments.value.map((f) => f.category).filter(Boolean)))])
const targetOptions = computed(() => ['all', ...Array.from(new Set(fragments.value.map((f) => String(f.target_agent_id ?? '')).filter((v) => v)))])

function effectivenessVariant(score: number): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (score >= 0.7) return 'default'
  if (score >= 0.4) return 'outline'
  if (score > 0) return 'secondary'
  return 'destructive'
}

const filteredFragments = computed(() => {
  const q = fragQuery.value.trim().toLowerCase()
  return fragments.value.filter((f) => {
    if (fragScopeFilter.value !== 'all' && f.scope !== fragScopeFilter.value) return false
    if (fragCategoryFilter.value !== 'all' && f.category !== fragCategoryFilter.value) return false
    if (fragTargetFilter.value !== 'all' && String(f.target_agent_id ?? '') !== fragTargetFilter.value) return false
    if (fragEnabledFilter.value !== 'all') {
      const want = fragEnabledFilter.value === 'enabled'
      if (Boolean(f.enabled) !== want) return false
    }
    if (!q) return true
    return [f.title, f.key, f.category, f.scope].some((v) => String(v).toLowerCase().includes(q))
  }).sort((a, b) => (b.priority ?? 0) - (a.priority ?? 0))
})

const pipelineCanvasPipeline = computed(() => {
  const id = pipelineCanvasSelectedId.value ?? pipelines.value[0]?.id ?? null
  const base = pipelines.value.find((p) => p.id === id) ?? pipelines.value[0] ?? null
  if (!base) return null
  if (base.id !== (pipelineCanvasSelectedId.value ?? pipelines.value[0]?.id)) return base
  if (pipelineCanvasNodes.value.length === 0 && pipelineCanvasEdges.value.length === 0) return base
  return { ...base, nodes: pipelineCanvasNodes.value as never, edges: pipelineCanvasEdges.value as never }
})

async function loadPipelines() {
  try {
    const list = await api.listPromptPipelines()
    pipelines.value = Array.isArray(list) ? list : []
  } catch (e) {
    notify.error(e)
  }
}
function openPipelineCreate() {
  editingPipelineId.value = null
  pipelineDraft.value = { name: '', description: '', scope: 'global' }
  showPipelineForm.value = true
}
function openPipelineEdit(p: PromptPipelineDto) {
  editingPipelineId.value = p.id
  pipelineDraft.value = { ...p }
  showPipelineForm.value = true
}
async function savePipelineDraft() {
  try {
    if (editingPipelineId.value) {
      const etag = (pipelines.value.find((x) => x.id === editingPipelineId.value) as unknown as { etag?: string })?.etag ?? null
      await api.updatePromptPipelineDraft(editingPipelineId.value, pipelineDraft.value, etag)
    } else {
      await api.createPromptPipelineDraft(pipelineDraft.value)
    }
    showPipelineForm.value = false
    await loadPipelines()
    notify.success({ message: '提示词草稿已保存' })
  } catch (e) {
    notify.error(e)
  }
}
async function publishPipeline(id: string) {
  try {
    await api.publishPromptPipeline(id)
    notify.success({ message: '提示词管线已发布' })
    await loadPipelines()
  } catch (e) {
    notify.error(e)
  }
}

async function loadPromptEngine() {
  fragLoading.value = true
  try {
    const [fragmentList, effList] = await Promise.all([
      api.listPromptFragments(),
      api.listAllPromptFragmentEffectiveness().catch(() => [] as PromptFragmentEffectivenessDto[]),
    ])
    fragments.value = Array.isArray(fragmentList) ? fragmentList : []
    effectivenessList.value = Array.isArray(effList) ? effList : []
    if (!selectedFragmentId.value && fragments.value.length > 0) await selectFragment(fragments.value[0])
    dismissByKey('prompts')
  } catch (err) {
    status.error({
      key: 'prompts',
      title: '加载提示词失败',
      message: err instanceof Error ? err.message : String(err),
      source: 'prompts',
      action: { label: '重试', run: loadPromptEngine }
    })
  } finally {
    fragLoading.value = false
  }
}
async function selectFragment(fragment: PromptFragmentDto) {
  const fid = fragment.id
  selectedFragmentId.value = fid
  fragmentVersions.value = []
  fragmentEffectiveness.value = null
  compareResult.value = null
  compareVersionA.value = null
  compareVersionB.value = null
  signalVersion.value = null
  try {
    const [versionList, eff] = await Promise.all([
      api.listPromptFragmentVersions(fid),
      api.getPromptFragmentEffectiveness(fid).catch(() => null),
    ])
    if (selectedFragmentId.value !== fid) return
    fragmentVersions.value = Array.isArray(versionList) ? versionList : []
    fragmentEffectiveness.value = eff as PromptFragmentEffectivenessDto | null
    if (fragmentVersions.value.length > 0) {
      compareVersionA.value = fragmentVersions.value[fragmentVersions.value.length - 1].version
      compareVersionB.value = fragmentVersions.value[0].version
    }
  } catch (e) {
    notify.error(e, { title: '加载片段详情失败', source: 'prompts' })
  }
}
function openNewVersion() {
  showNewVersion.value = true
  newVersionContent.value = selectedFragment.value?.content ?? ''
  newVersionChangedFields.value = 'content'
  newVersionSummary.value = ''
}
async function createVersion() {
  if (!selectedFragment.value || !newVersionContent.value.trim()) return
  fragBusy.value = true
  try {
    await api.createPromptFragmentVersion(selectedFragment.value.id, {
      content: newVersionContent.value,
      changed_fields: newVersionChangedFields.value.split(',').map((s) => s.trim()).filter(Boolean),
      change_summary: newVersionSummary.value || 'Updated content'
    })
    showNewVersion.value = false
    await selectFragment(selectedFragment.value)
    notify.success({ message: '已创建新版本', source: 'prompts' })
  } catch (e) {
    notify.error(e, { title: '创建版本失败', source: 'prompts' })
  } finally {
    fragBusy.value = false
  }
}
async function rollbackVersion(targetVersion: number) {
  const frag = selectedFragment.value
  if (!frag) return
  if (!await confirm({ title: '回滚提示词', message: `回滚 ${frag.title} 至 v${targetVersion}？`, confirmLabel: '回滚', cancelLabel: '取消', destructive: true })) return
  fragBusy.value = true
  try {
    const updated = await api.rollbackPromptFragment(frag.id, targetVersion)
    const idx = fragments.value.findIndex((f) => f.id === updated.id)
    if (idx >= 0) fragments.value[idx] = updated
    await selectFragment(updated)
    notify.success({ message: `已回滚至 v${targetVersion}`, source: 'prompts' })
  } catch (e) {
    notify.error(e, { title: `回滚 v${targetVersion} 失败`, source: 'prompts' })
  } finally {
    fragBusy.value = false
  }
}
async function recordSignal(signal: 'positive' | 'negative') {
  const frag = selectedFragment.value
  if (!frag) return
  fragBusy.value = true
  try {
    const result = await api.recordPromptFragmentSignal(frag.id, { signal, note: signalNote.value || null, version: signalVersion.value ?? undefined })
    if (selectedFragmentId.value === frag.id) fragmentEffectiveness.value = result
    signalNote.value = ''
    effectivenessList.value = await api.listAllPromptFragmentEffectiveness().catch(() => effectivenessList.value)
    notify.success({ message: `${signal === 'positive' ? '正向' : '负向'}信号已记录`, source: 'prompts' })
  } catch (e) {
    notify.error(e, { title: '记录信号失败', source: 'prompts' })
  } finally {
    fragBusy.value = false
  }
}
async function compareVersions() {
  const frag = selectedFragment.value
  const a = compareVersionA.value
  const b = compareVersionB.value
  if (!frag || a === null || b === null) return
  fragBusy.value = true
  try {
    const result = await api.comparePromptFragmentVersions(frag.id, a, b)
    if (selectedFragmentId.value === frag.id && compareVersionA.value === a && compareVersionB.value === b) compareResult.value = result
  } catch (e) {
    notify.error(e, { title: '对比失败', source: 'prompts' })
  } finally {
    fragBusy.value = false
  }
}
async function generatePromptPreview() {
  previewBusy.value = true
  try {
    promptPreview.value = await api.previewPromptContext({
      agent_id: promptPreviewAgentId.value || 'agent_meeting',
      mode: promptPreviewMode.value || null,
      session_id: promptPreviewSessionId.value || null,
      run_id: promptPreviewRunId.value || null,
      user_content: promptPreviewUserContent.value || null
    })
  } catch (e) {
    notify.error(e, { title: '预览失败' })
  } finally {
    previewBusy.value = false
  }
}
function handlePipelineCanvasUpdateNodes(nodes: unknown[]) { pipelineCanvasNodes.value = nodes }
function handlePipelineCanvasUpdateEdges(edges: unknown[]) { pipelineCanvasEdges.value = edges }

// ── Section 4: Evolution ──
const proposals = ref<AgentEvolutionProposalDto[]>([])
const evolutionLoading = ref(false)
const evolutionBusy = ref(false)
const selectedProposalId = ref('')
const generateSessionId = ref('')
const generateLookback = ref('200')
const rejectReason = ref('')
const showPromoteSheet = ref(false)
const promoteForm = ref<PromoteAgentCandidateInput>({
  agent_id: '',
  mode: 'plan',
  model_route_purpose: 'chat',
  allowed_tools: [],
  capabilities: [],
  system_prompt: null
})
const promoteToolInput = ref('')
const promoteCapabilityInput = ref('')
const candidates = ref<AgentCandidateDto[]>([])

async function loadCandidates() {
  try {
    const list = await api.listCandidates()
    candidates.value = Array.isArray(list) ? list : []
  } catch { /* optional */ }
}
async function loadProposals() {
  evolutionLoading.value = true
  try {
    const list = await api.listEvolutionProposals()
    proposals.value = Array.isArray(list) ? list : []
    if (!selectedProposalId.value && proposals.value.length > 0) selectedProposalId.value = proposals.value[0].id
    dismissByKey('evolution')
  } catch (err) {
    status.error({
      key: 'evolution',
      title: '加载演化候选失败',
      message: err instanceof Error ? err.message : String(err),
      source: 'evolution',
      action: { label: '重试', run: loadProposals }
    })
  } finally {
    evolutionLoading.value = false
  }
}
async function loadEvolution() {
  await Promise.all([loadProposals(), loadCandidates()])
}
const selectedProposal = computed(() => proposals.value.find((p) => p.id === selectedProposalId.value) ?? null)
watch(selectedProposalId, () => { rejectReason.value = '' })
const sortedProposals = computed(() => [...proposals.value].sort((a, b) => b.confidence_score - a.confidence_score))

function confidenceVariant(score: number): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (score >= 0.7) return 'default'
  if (score >= 0.4) return 'outline'
  return 'secondary'
}
function evolutionStatusVariant(s: string): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (s === 'promoted') return 'default'
  if (s === 'rejected') return 'destructive'
  if (s === 'evaluating') return 'outline'
  return 'secondary'
}
function evolutionStatusLabel(s: string) {
  const m: Record<string, string> = { proposed: 'Proposed', promoted: 'Promoted', rejected: 'Rejected', evaluating: 'Evaluating' }
  return m[s] ?? s
}
async function generateProposals() {
  evolutionBusy.value = true
  try {
    const params: { session_id?: string; lookback_event_count?: number } = {}
    if (generateSessionId.value.trim()) params.session_id = generateSessionId.value.trim()
    const n = Number(generateLookback.value)
    if (n > 0) params.lookback_event_count = n
    const generated = await api.generateEvolutionProposals(params)
    proposals.value = Array.isArray(generated) ? generated : []
    if (proposals.value.length > 0) selectedProposalId.value = proposals.value[0].id
    notify.success({ message: `已提炼生成 ${proposals.value.length} 个候选提案`, source: 'evolution' })
  } catch (e) {
    notify.error(e, { title: '生成提案失败', source: 'evolution' })
  } finally {
    evolutionBusy.value = false
  }
}
function openPromoteModal(p: AgentEvolutionProposalDto) {
  selectedProposalId.value = p.id
  const baseId = `agent_${p.agent_type}_${Date.now().toString(36)}`
  promoteForm.value = {
    agent_id: baseId,
    mode: p.layer === 'planning' ? 'plan' : 'execute',
    model_route_purpose: p.layer === 'planning' ? 'planner' : 'chat',
    allowed_tools: [...(p.suggested_tools ?? [])],
    capabilities: [],
    system_prompt: null
  }
  promoteToolInput.value = ''
  promoteCapabilityInput.value = ''
  showPromoteSheet.value = true
}
function addPromoteTool() {
  const v = promoteToolInput.value.trim()
  if (v && !promoteForm.value.allowed_tools.includes(v)) {
    promoteForm.value.allowed_tools.push(v)
    promoteToolInput.value = ''
  }
}
function removePromoteTool(t: string) {
  const i = promoteForm.value.allowed_tools.indexOf(t)
  if (i >= 0) promoteForm.value.allowed_tools.splice(i, 1)
}
function addPromoteCap() {
  const v = promoteCapabilityInput.value.trim()
  if (v && !promoteForm.value.capabilities.includes(v)) {
    promoteForm.value.capabilities.push(v)
    promoteCapabilityInput.value = ''
  }
}
function removePromoteCap(c: string) {
  const i = promoteForm.value.capabilities.indexOf(c)
  if (i >= 0) promoteForm.value.capabilities.splice(i, 1)
}
async function submitPromote() {
  const p = selectedProposal.value
  if (!p || !promoteForm.value.agent_id.trim()) return
  evolutionBusy.value = true
  try {
    await api.promoteAgentCandidate(p.id, promoteForm.value)
    await loadProposals()
    showPromoteSheet.value = false
    notify.success({ message: t('agentCenter.evolution.promoteSuccess'), source: 'evolution' })
  } catch (e) {
    notify.error(e, { title: '晋升失败', source: 'evolution' })
  } finally {
    evolutionBusy.value = false
  }
}
async function rejectEvolutionCandidate(p: AgentEvolutionProposalDto) {
  const reason = rejectReason.value.trim() || undefined
  if (!await confirm({ title: '拒绝候选', message: reason ? `拒绝 ${p.name}？原因：${reason}` : `拒绝 ${p.name}？`, confirmLabel: '拒绝', cancelLabel: '取消', destructive: true })) return
  evolutionBusy.value = true
  try {
    await api.rejectAgentCandidate(p.id, reason)
    rejectReason.value = ''
    await loadProposals()
    notify.success({ message: `${p.name} 已拒绝`, source: 'evolution' })
  } catch (e) {
    notify.error(e, { title: `拒绝 ${p.name} 失败`, source: 'evolution' })
  } finally {
    evolutionBusy.value = false
  }
}

// ── Section 5: Runtime Instances & Info ──
const runtimeRunId = ref('')
const runtimeSessionFilter = ref('')
const runtimeStatusFilter = ref<'all' | string>('all')
const runtimePage = ref(1)
const runtimePageSize = ref(10)
const runtimeInstances = ref<AgentRuntimeInstanceDto[]>([])
const runtimeControlBusy = ref<string | null>(null)
const reassignForm = ref({ session_id: '', interaction_id: '', target_run_id: '' })
const showReassignSheet = ref(false)
let runtimeTimer: number | null = null

async function loadRuntimeInstances() {
  try {
    runtimeInstances.value = await api.listRuntimeInstances(runtimeRunId.value || undefined)
  } catch (e) {
    notify.error(e)
  }
}
function startRuntimePoll() {
  stopRuntimePoll()
  loadRuntimeInstances()
  runtimeTimer = window.setInterval(loadRuntimeInstances, 3000)
}
function stopRuntimePoll() {
  if (runtimeTimer) {
    clearInterval(runtimeTimer)
    runtimeTimer = null
  }
}
const runtimeStatusOptions = computed(() => ['all', ...Array.from(new Set(runtimeInstances.value.map((r) => String(r.status ?? '')).filter(Boolean)))])
const filteredRuntimeInstances = computed(() => {
  const qs = runtimeSessionFilter.value.trim().toLowerCase()
  const st = runtimeStatusFilter.value
  return runtimeInstances.value.filter((r) => {
    if (st !== 'all' && String(r.status) !== st) return false
    if (qs && !String(r.session_id ?? r.run_id ?? '').toLowerCase().includes(qs)) return false
    return true
  })
})
const runtimeTotalPages = computed(() => Math.max(1, Math.ceil(filteredRuntimeInstances.value.length / runtimePageSize.value)))
const pagedRuntimeInstances = computed(() => {
  const start = (runtimePage.value - 1) * runtimePageSize.value
  return filteredRuntimeInstances.value.slice(start, start + runtimePageSize.value)
})
watch([runtimeSessionFilter, runtimeStatusFilter], () => { runtimePage.value = 1 })
watch(filteredRuntimeInstances, () => { if (runtimePage.value > runtimeTotalPages.value) runtimePage.value = runtimeTotalPages.value })

async function controlInstance(runId: string, action: 'pause' | 'resume' | 'cancel') {
  const labels: Record<string, string> = { pause: '暂停', resume: '恢复', cancel: '取消' }
  if (!await confirm({
    title: `确认${labels[action]}`,
    message: `确认对 run ${runId.slice(0, 8)} 执行 ${labels[action]}？`,
    confirmLabel: labels[action],
    cancelLabel: '返回',
    destructive: action === 'cancel'
  })) return
  runtimeControlBusy.value = `${runId}:${action}`
  try {
    await generatedApi.controlRun(runId, { action })
    notify.success({ message: `${labels[action]} 已发送` })
    await loadRuntimeInstances()
  } catch (e) {
    notify.error(e, { title: `${labels[action]}失败` })
  } finally {
    runtimeControlBusy.value = null
  }
}
function openReassignForm(targetRunId: string) {
  reassignForm.value = { session_id: '', interaction_id: '', target_run_id: targetRunId }
  showReassignSheet.value = true
}
async function submitReassign() {
  const { session_id, interaction_id, target_run_id } = reassignForm.value
  if (!session_id.trim() || !interaction_id.trim() || !target_run_id.trim()) {
    notify.error({ message: '请完整填写 session_id / interaction_id / target_run_id' })
    return
  }
  try {
    await api.reassignInteraction(session_id.trim(), interaction_id.trim(), { target_run_id: target_run_id.trim() })
    notify.success({ message: '任务改派成功' })
    showReassignSheet.value = false
  } catch (e) {
    notify.error(e, { title: '改派失败' })
  }
}

// ── Inspector & Overview Dynamic Metrics ──
const enabledAgentsCount = computed(() => agents.value.filter((a) => a.enabled).length)
const draftAgentsCount = computed(() => agents.value.filter((a) => (a.status ?? 'draft') === 'draft').length)
const proposedCount = computed(() =>
  (proposals.value.length > 0 ? proposals.value : candidates.value).filter((c) => (c as unknown as { status: string }).status === 'proposed').length
)
const activeModesCount = computed(() => modes.value.filter((m) => (m.status ?? 'draft') === 'published').length)

// Selected Agent for Inspector
const selectedAgentForInspector = computed(() => {
  if (expandedAgentId.value) return agents.value.find((a) => a.id === expandedAgentId.value) ?? null
  return agents.value[0] ?? null
})

// Dynamic Rail tabs
const railSections = computed(() => [
  { key: 'config' as const, label: t('agentCenter.tabs.config'), count: agents.value.length, icon: Bot },
  { key: 'topology' as const, label: t('agentCenter.tabs.topology'), count: modes.value.length, icon: Workflow },
  { key: 'prompt' as const, label: t('agentCenter.tabs.prompt'), count: pipelines.value.length, icon: FileText },
  { key: 'evolution' as const, label: t('agentCenter.tabs.evolution'), count: proposedCount.value, icon: Dna },
  { key: 'info' as const, label: t('agentCenter.tabs.info'), count: runtimeInstances.value.length, icon: Info },
])

// Global Refresh
async function refreshAll() {
  await Promise.all([
    loadAgents(),
    loadModes(),
    loadPipelines(),
    loadEvolution(),
    loadRuntimeInstances(),
    loadManifest(),
    loadPromptEngine()
  ])
}

onMounted(() => {
  refreshAll()
})
</script>

<template>
  <div class="center-page agent-center-page">
    <!-- Command Bar (Header) -->
    <div class="center-command-bar">
      <div>
        <span class="center-kicker">{{ t('agentCenter.kicker') }}</span>
        <h2>{{ t('agentCenter.title') }}</h2>
        <p>{{ t('agentCenter.subtitle') }}</p>
      </div>
      <div class="center-command-actions">
        <UiButton v-if="activeSection === 'config'" size="sm" @click="openCreateAgent">
          <Plus :size="14" />
          <span>{{ t('agentCenter.createAgent') }}</span>
        </UiButton>
        <UiButton v-else-if="activeSection === 'topology'" size="sm" @click="createMode">
          <Workflow :size="14" />
          <span>{{ t('agentCenter.createMode') }}</span>
        </UiButton>
        <UiButton v-else-if="activeSection === 'prompt'" size="sm" @click="openPipelineCreate">
          <Plus :size="14" />
          <span>新建管线</span>
        </UiButton>
        <UiButton v-else-if="activeSection === 'evolution'" size="sm" :disabled="evolutionBusy" @click="generateProposals">
          <Dna :size="14" />
          <span>{{ t('agentCenter.generateProposal') }}</span>
        </UiButton>
        <UiButton size="sm" variant="outline" :disabled="agentsLoading" @click="refreshAll">
          <RefreshCw :size="14" :class="{ 'animate-spin': agentsLoading }" />
          <span>{{ t('agentCenter.refresh') }}</span>
        </UiButton>
      </div>
    </div>

    <!-- Overview Receipts 3格卡片 (对齐 Model Center 规范) -->
    <section class="center-overview-receipt" aria-label="overview">
      <div class="center-receipt-item ready" @click="activeSection = 'config'" style="cursor: pointer">
        <Settings2 :size="17" />
        <div>
          <span>{{ t('agentCenter.receiptConfigTitle') }}</span>
          <strong>{{ t('agentCenter.receiptConfigDesc', { published: publishedCount, total: agents.length }) }}</strong>
        </div>
        <UiBadge variant="default">可写</UiBadge>
      </div>
      <div class="center-receipt-item configured" @click="activeSection = 'topology'" style="cursor: pointer">
        <Workflow :size="17" />
        <div>
          <span>{{ t('agentCenter.receiptTopologyTitle') }}</span>
          <strong>{{ t('agentCenter.receiptTopologyDesc', { modes: activeModesCount }) }}</strong>
        </div>
        <UiBadge variant="outline">{{ modes.length }} 模式</UiBadge>
      </div>
      <div class="center-receipt-item preview" :class="{ ready: proposedCount > 0 }" @click="activeSection = 'evolution'" style="cursor: pointer">
        <Dna :size="17" />
        <div>
          <span>{{ t('agentCenter.receiptRuntimeTitle') }}</span>
          <strong>{{ t('agentCenter.receiptRuntimeDesc', { instances: runtimeInstances.length, proposals: proposedCount }) }}</strong>
        </div>
        <UiBadge :variant="proposedCount > 0 ? 'default' : 'secondary'">{{ proposedCount }} 候选</UiBadge>
      </div>
    </section>

    <!-- Workbench (3栏体系: Inspector | Rail | Stage) -->
    <div class="center-workbench agent-workbench">
      <!-- 1. Inspector 动态检查器 -->
      <aside class="center-inspector" aria-label="inspector">
        <div class="center-pane-heading">
          <div>
            <span>Inspector</span>
            <strong>概览与诊断</strong>
          </div>
          <Info :size="16" />
        </div>

        <!-- 4格健康指标 -->
        <section class="model-health-overview">
          <div class="model-health-head">
            <div>
              <h3>健康状态</h3>
              <span>{{ activeSection === 'topology' ? '拓扑节点与连线检查' : '双层编排与实例概览' }}</span>
            </div>
          </div>
          <template v-if="activeSection === 'topology'">
            <div class="model-health-metrics">
              <div><span>节点数</span><strong>{{ modeNodes.length }}</strong></div>
              <div><span>边连线</span><strong>{{ modeEdges.length }}</strong></div>
              <div :class="{ attention: unboundNodeCount > 0 }"><span>未绑定</span><strong>{{ unboundNodeCount }}</strong></div>
              <div :class="{ attention: isolatedNodeIds.length > 0 }"><span>孤岛节点</span><strong>{{ isolatedNodeIds.length }}</strong></div>
            </div>
            <div v-if="healthWarnings.length" class="model-health-alert">
              <Info :size="16" />
              <div><strong>{{ healthWarnings.join(' · ') }}</strong><span>请在发布前核对拓扑连线</span></div>
            </div>
          </template>
          <template v-else>
            <div class="model-health-metrics">
              <div><span>{{ t('agentCenter.metrics.publishedAgents') }}</span><strong>{{ publishedCount }}</strong></div>
              <div><span>{{ t('agentCenter.metrics.activeModes') }}</span><strong>{{ activeModesCount }}</strong></div>
              <div :class="{ attention: proposedCount > 0 }"><span>{{ t('agentCenter.metrics.pendingProposals') }}</span><strong>{{ proposedCount }}</strong></div>
              <div><span>{{ t('agentCenter.metrics.activeInstances') }}</span><strong>{{ runtimeInstances.length }}</strong></div>
            </div>
          </template>
        </section>

        <!-- 选中项动态详情 (Inspector Dynamic Card) -->
        <section v-if="activeSection === 'config' && selectedAgentForInspector" class="inspector-provider-detail">
          <div class="provider-detail-head compact">
            <span class="provider-brand-icon"><Bot :size="16" /></span>
            <div class="provider-detail-info">
              <strong>{{ selectedAgentForInspector.name }}</strong>
              <span class="provider-detail-driver">{{ normalizeLayer(selectedAgentForInspector.layer) }} · {{ agentTypeLabel(selectedAgentForInspector.agent_type) }}</span>
            </div>
            <UiBadge :variant="readinessVariant(selectedAgentForInspector.status)">{{ selectedAgentForInspector.status ?? 'draft' }}</UiBadge>
          </div>
          <div class="provider-detail-grid compact">
            <div class="provider-detail-cell">
              <span class="provider-detail-label">架构分层</span>
              <span class="provider-detail-value">{{ selectedAgentForInspector.layer }}</span>
            </div>
            <div class="provider-detail-cell">
              <span class="provider-detail-label">工具授权数</span>
              <span class="provider-detail-value">{{ (selectedAgentForInspector.allowed_tools ?? []).length }}</span>
            </div>
            <div class="provider-detail-cell" style="grid-column: 1 / -1">
              <span class="provider-detail-label">Revision 并发标记</span>
              <span class="provider-detail-value provider-detail-mono">{{ (selectedAgentForInspector as unknown as { etag?: string })?.etag ?? String(selectedAgentForInspector.revision ?? '—') }}</span>
            </div>
          </div>
          <div v-if="selectedAgentForInspector.description" class="provider-status-note compact">
            <span class="quiet">{{ selectedAgentForInspector.description }}</span>
          </div>
        </section>

        <section v-else-if="activeSection === 'topology' && selectedMode" class="inspector-provider-detail">
          <div class="provider-detail-head compact">
            <span class="provider-brand-icon"><Workflow :size="16" /></span>
            <div class="provider-detail-info">
              <strong>{{ selectedMode.display_name }}</strong>
              <span class="provider-detail-driver">模式 ID: {{ selectedMode.id.slice(0, 8) }}</span>
            </div>
            <UiBadge :variant="readinessVariant(selectedMode.status)">{{ selectedMode.status ?? 'draft' }}</UiBadge>
          </div>
          <div class="provider-detail-grid compact">
            <div class="provider-detail-cell"><span class="provider-detail-label">节点数</span><span class="provider-detail-value">{{ modeNodes.length }}</span></div>
            <div class="provider-detail-cell"><span class="provider-detail-label">边连线</span><span class="provider-detail-value">{{ modeEdges.length }}</span></div>
          </div>
        </section>

        <section v-else-if="activeSection === 'prompt' && selectedFragment" class="inspector-provider-detail">
          <div class="provider-detail-head compact">
            <span class="provider-brand-icon"><FileText :size="16" /></span>
            <div class="provider-detail-info">
              <strong>{{ selectedFragment.title }}</strong>
              <span class="provider-detail-driver">{{ selectedFragment.key }}</span>
            </div>
            <UiBadge :variant="selectedFragment.enabled ? 'default' : 'secondary'">{{ selectedFragment.enabled ? 'enabled' : 'disabled' }}</UiBadge>
          </div>
          <div class="provider-detail-grid compact">
            <div class="provider-detail-cell"><span class="provider-detail-label">Scope</span><span class="provider-detail-value">{{ selectedFragment.scope }}</span></div>
            <div class="provider-detail-cell"><span class="provider-detail-label">Priority</span><span class="provider-detail-value">{{ selectedFragment.priority }}</span></div>
          </div>
        </section>

        <section v-else-if="activeSection === 'evolution' && selectedProposal" class="inspector-provider-detail">
          <div class="provider-detail-head compact">
            <span class="provider-brand-icon"><Dna :size="16" /></span>
            <div class="provider-detail-info">
              <strong>{{ selectedProposal.name }}</strong>
              <span class="provider-detail-driver">{{ normalizeLayer(selectedProposal.layer) }} · {{ selectedProposal.agent_type }}</span>
            </div>
            <UiBadge :variant="confidenceVariant(selectedProposal.confidence_score)">{{ (selectedProposal.confidence_score * 100).toFixed(0) }}%</UiBadge>
          </div>
          <div class="provider-detail-grid compact">
            <div class="provider-detail-cell"><span class="provider-detail-label">建议工具</span><span class="provider-detail-value">{{ (selectedProposal.suggested_tools ?? []).length }} 个</span></div>
            <div class="provider-detail-cell"><span class="provider-detail-label">提炼源</span><span class="provider-detail-value">{{ selectedProposal.generated_by_agent_id }}</span></div>
          </div>
        </section>

        <div v-else class="center-empty-state inspector-empty">
          <Info :size="16" />
          <span>在右侧选择一项查看动态详情</span>
        </div>

        <!-- 治理诊断折叠抽屉 -->
        <details class="model-diagnostics" style="margin-top: 10px">
          <summary>
            <span>{{ t('agentCenter.diagnostics.title') }}</span>
            <ChevronRight :size="14" />
          </summary>
          <div class="model-diagnostics-grid">
            <section class="model-diagnostic-section">
              <strong>{{ t('agentCenter.diagnostics.manifestTitle') }}</strong>
              <span class="quiet">{{ t('agentCenter.diagnostics.manifestDesc', { count: manifestToolList.length }) }}</span>
            </section>
            <section class="model-diagnostic-section">
              <strong>{{ t('agentCenter.diagnostics.layerRules') }}</strong>
              <span class="quiet">{{ t('agentCenter.diagnostics.layerRulesDesc') }}</span>
            </section>
            <section class="model-diagnostic-section">
              <strong>{{ t('agentCenter.diagnostics.conflictDetection') }}</strong>
              <span class="quiet">{{ t('agentCenter.diagnostics.conflictDetectionDesc') }}</span>
            </section>
          </div>
        </details>
      </aside>

      <!-- 2. Resource Rail 导航栏 -->
      <aside class="center-resource-rail" aria-label="resources">
        <div class="center-pane-heading">
          <div>
            <span>资源</span>
            <strong>功能模块</strong>
          </div>
        </div>
        <div class="model-center-tabs" role="tablist" aria-label="agent-center-rail" style="grid-template-columns: 1fr">
          <button
            v-for="s in railSections"
            :key="s.key"
            role="tab"
            :aria-selected="activeSection === s.key"
            :class="{ active: activeSection === s.key }"
            @click="activeSection = s.key"
          >
            <span style="display: flex; align-items: center; gap: 8px">
              <component :is="s.icon" :size="14" />
              {{ s.label }}
            </span>
            <UiBadge variant="secondary">{{ s.count }}</UiBadge>
          </button>
        </div>
      </aside>

      <!-- 3. Stage 主舞台 -->
      <main class="center-resource-stage">
        <!-- 3.1 智能体配置 (CONFIG) -->
        <section v-if="activeSection === 'config'" class="center-resource-section">
          <div class="center-resource-heading">
            <div>
              <h3>{{ t('agentCenter.tabs.config') }}</h3>
              <p>定义并发布智能体的分层职责、工具授权与提示词基线。</p>
            </div>
            <UiBadge variant="outline">{{ filteredAgents.length }}/{{ agents.length }}</UiBadge>
          </div>

          <!-- 工具栏 (对齐 model-provider-toolbar) -->
          <div class="model-provider-toolbar">
            <div class="model-provider-search">
              <Search :size="15" />
              <UiInput v-model="agentQuery" :placeholder="t('agentCenter.filter.searchAgents')" />
            </div>
            <div class="model-provider-filters" role="group" aria-label="layer">
              <button :class="{ active: agentLayerFilter === 'all' }" :aria-pressed="agentLayerFilter === 'all'" @click="agentLayerFilter = 'all'">{{ t('agentCenter.filter.allLayers') }}</button>
              <button :class="{ active: agentLayerFilter === 'operation' }" :aria-pressed="agentLayerFilter === 'operation'" @click="agentLayerFilter = 'operation'">Operation</button>
              <button :class="{ active: agentLayerFilter === 'execution' }" :aria-pressed="agentLayerFilter === 'execution'" @click="agentLayerFilter = 'execution'">Execution</button>
            </div>
            <div class="model-provider-filters" role="group" aria-label="status">
              <button :class="{ active: agentStatusFilter === 'all' }" :aria-pressed="agentStatusFilter === 'all'" @click="agentStatusFilter = 'all'">{{ t('agentCenter.filter.allStatuses') }}</button>
              <button :class="{ active: agentStatusFilter === 'draft' }" :aria-pressed="agentStatusFilter === 'draft'" @click="agentStatusFilter = 'draft'">{{ t('agentCenter.filter.draft') }}</button>
              <button :class="{ active: agentStatusFilter === 'published' }" :aria-pressed="agentStatusFilter === 'published'" @click="agentStatusFilter = 'published'">{{ t('agentCenter.filter.published') }}</button>
              <button :class="{ active: agentStatusFilter === 'archived' }" :aria-pressed="agentStatusFilter === 'archived'" @click="agentStatusFilter = 'archived'">{{ t('agentCenter.filter.archived') }}</button>
            </div>
            <span class="model-provider-count">{{ t('agentCenter.filter.resultCount', { visible: filteredAgents.length, total: agents.length }) }}</span>
          </div>

          <!-- 骨架屏 -->
          <div v-if="agentsLoading" class="center-loading-state" aria-live="polite">
            <UiSkeleton v-for="i in 4" :key="i" class="center-loading-line" />
          </div>

          <!-- 智能体列表表格 -->
          <div v-else class="model-provider-table">
            <div class="model-provider-table-head" aria-hidden="true">
              <span>智能体名称</span>
              <span>架构分层</span>
              <span>授权工具数</span>
              <span>状态</span>
              <span>操作</span>
            </div>
            <template v-for="a in filteredAgents" :key="a.id">
              <div class="model-provider-row" :class="{ issue: (a.status ?? 'draft') === 'draft', active: expandedAgentId === a.id }">
                <button class="model-provider-identity" :aria-expanded="expandedAgentId === a.id" @click="selectAgentItem(a.id)">
                  <span class="provider-brand-icon"><Bot :size="16" /></span>
                  <span>
                    <strong :title="a.name">{{ a.name }}</strong>
                    <small :title="a.layer">{{ normalizeLayer(a.layer) }} · {{ agentTypeLabel(a.agent_type) }}</small>
                  </span>
                </button>
                <span class="model-provider-cell">
                  <UiBadge variant="outline">{{ normalizeLayer(a.layer) }}</UiBadge>
                </span>
                <span class="model-provider-cell">{{ (a.allowed_tools ?? []).length }} tools</span>
                <div class="model-provider-status">
                  <UiBadge :variant="readinessVariant(a.status)">{{ a.status ?? 'draft' }}</UiBadge>
                </div>
                <div class="model-provider-actions">
                  <UiButton variant="ghost" size="icon" title="编辑" @click="openEditAgent(a)"><Settings2 :size="14" /></UiButton>
                  <UiButton variant="ghost" size="icon" title="发布" @click="publishAgent(a.id)"><Plus :size="14" /></UiButton>
                  <UiButton variant="ghost" size="icon" class="provider-delete-btn" title="归档" @click="archiveAgent(a.id)"><FileText :size="14" /></UiButton>
                  <UiButton variant="ghost" size="icon" :aria-expanded="expandedAgentId === a.id" @click="selectAgentItem(a.id)">
                    <ChevronRight :size="14" class="provider-chevron" :class="{ open: expandedAgentId === a.id }" />
                  </UiButton>
                </div>
                <span class="model-provider-mobile-meta">{{ a.agent_type }} · {{ (a.allowed_tools ?? []).length }} tools</span>
              </div>

              <!-- 展开抽屉面板 (对齐 provider-detail-panel) -->
              <div v-if="expandedAgentId === a.id" class="provider-detail-panel">
                <div class="provider-detail-head">
                  <div class="provider-detail-info">
                    <strong>智能体详细配置</strong>
                    <span class="provider-detail-driver">Revision 并发隔离已就绪</span>
                  </div>
                  <UiBadge variant="outline">rev {{ a.revision ?? '—' }}</UiBadge>
                </div>
                <div class="provider-detail-grid">
                  <div class="provider-detail-cell">
                    <span class="provider-detail-label">能力标签 (Capabilities)</span>
                    <span class="provider-detail-value">
                      <span v-for="c in (a.capabilities ?? [])" :key="c" class="provider-cap-tag">{{ c }}</span>
                      <span v-if="(a.capabilities ?? []).length === 0" class="quiet">—</span>
                    </span>
                  </div>
                  <div class="provider-detail-cell">
                    <span class="provider-detail-label">授权工具 (Allowed Tools)</span>
                    <span class="provider-detail-value">
                      <span v-for="t in (a.allowed_tools ?? [])" :key="t" class="provider-cap-tag">{{ t }}</span>
                      <span v-if="(a.allowed_tools ?? []).length === 0" class="quiet">—</span>
                    </span>
                  </div>
                  <div class="provider-detail-cell">
                    <span class="provider-detail-label">ETag / Revision</span>
                    <span class="provider-detail-value provider-detail-mono">{{ (a as unknown as { etag?: string }).etag ?? String(a.revision ?? '—') }}</span>
                  </div>
                  <div class="provider-detail-cell">
                    <span class="provider-detail-label">更新时间</span>
                    <span class="provider-detail-value">{{ a.updated_at ? formatDate(a.updated_at) : '—' }}</span>
                  </div>
                </div>
                <UiCollapsible v-if="a.system_prompt" class="provider-detail-section">
                  <template #trigger><span class="provider-detail-section-title">System Prompt</span></template>
                  <p class="quiet" style="white-space: pre-wrap; margin-top: 6px; font-size: 12px; line-height: 1.5">{{ a.system_prompt }}</p>
                </UiCollapsible>
                <div v-if="agentVersions.length" class="provider-detail-section" style="margin-top: 10px">
                  <div class="provider-detail-section-title">版本时间线</div>
                  <div v-for="v in agentVersions" :key="String((v as Record<string, unknown>).id)" class="provider-status-note compact" style="justify-content: space-between">
                    <span class="quiet">v{{ String((v as Record<string, unknown>).version ?? (v as Record<string, unknown>).id) }}</span>
                    <span class="quiet">{{ String((v as Record<string, unknown>).created_at ?? '') }}</span>
                  </div>
                </div>
              </div>
            </template>

            <!-- 空状态 -->
            <div v-if="filteredAgents.length === 0" class="model-provider-empty">
              <Search :size="24" />
              <span>{{ t('agentCenter.empty.noAgents') }}</span>
              <p class="quiet" style="font-size: 12px; margin: 4px 0 10px">{{ t('agentCenter.empty.noAgentsHint') }}</p>
              <UiButton variant="outline" size="sm" @click="openCreateAgent">
                <Plus :size="14" />
                <span>{{ t('agentCenter.createAgent') }}</span>
              </UiButton>
            </div>
          </div>

          <!-- 智能体新建/编辑抽屉 (UiSheet) -->
          <UiSheet :open="showAgentForm" side="right" @update:open="showAgentForm = $event">
            <div style="display: grid; gap: 14px; min-width: 380px; max-width: 460px; padding: 4px 0">
              <div class="provider-detail-head">
                <div class="provider-detail-info">
                  <h3>{{ editingAgentId ? t('agentCenter.agentForm.editTitle') : t('agentCenter.agentForm.createTitle') }}</h3>
                  <span class="provider-detail-driver">配置智能体定义与分层角色</span>
                </div>
              </div>

              <div style="display: grid; gap: 10px">
                <div>
                  <UiLabel>{{ t('agentCenter.agentForm.name') }}</UiLabel>
                  <UiInput v-model="agentDraft.name as string" :placeholder="t('agentCenter.agentForm.namePlaceholder')" />
                </div>
                <div style="display: grid; grid-template-columns: 1fr 1fr; gap: 10px">
                  <div>
                    <UiLabel>{{ t('agentCenter.agentForm.layer') }}</UiLabel>
                    <select v-model="agentDraft.layer" class="settings-select">
                      <option value="operation">operation (运营层)</option>
                      <option value="execution">execution (执行层)</option>
                    </select>
                  </div>
                  <div>
                    <UiLabel>{{ t('agentCenter.agentForm.agentType') }}</UiLabel>
                    <UiInput v-model="agentDraft.agent_type as string" placeholder="assistant / supervisor" />
                  </div>
                </div>
                <div>
                  <UiLabel>{{ t('agentCenter.agentForm.description') }}</UiLabel>
                  <UiTextarea v-model="agentDraft.description as string" :placeholder="t('agentCenter.agentForm.descriptionPlaceholder')" :rows="2" />
                </div>

                <!-- 工具授权拾取器 -->
                <div>
                  <UiLabel>{{ t('agentCenter.agentForm.tools') }}</UiLabel>
                  <div style="display: flex; gap: 8px; margin: 6px 0">
                    <UiInput v-model="agentToolInput" :placeholder="t('agentCenter.agentForm.toolPlaceholder')" @keydown.enter.prevent="addTool" style="flex: 1" />
                    <UiButton size="sm" variant="outline" @click="addTool">添加</UiButton>
                  </div>
                  <div class="model-capability-row" style="flex-wrap: wrap; gap: 4px; margin-bottom: 6px">
                    <span v-for="(tool, i) in (agentDraft.allowed_tools ?? [])" :key="tool" class="provider-cap-tag" style="cursor: pointer" @click="removeTool(i)">{{ tool }} ×</span>
                  </div>
                  <span class="quiet" style="font-size: 11px">{{ t('agentCenter.agentForm.manifestToolPicker') }}：</span>
                  <div v-if="manifestToolList.length" class="tool-picker" style="display: flex; flex-wrap: wrap; gap: 6px; margin-top: 6px; max-height: 120px; overflow-y: auto">
                    <button
                      v-for="tool in manifestToolList.slice(0, 30)"
                      :key="tool.id"
                      class="provider-cap-tag"
                      :style="{ opacity: (agentDraft.allowed_tools ?? []).includes(tool.id) ? '1' : '0.5' }"
                      @click="toggleManifestTool(tool.id)"
                    >
                      {{ tool.id }}
                    </button>
                  </div>
                </div>

                <!-- 能力标签 -->
                <div>
                  <UiLabel>{{ t('agentCenter.agentForm.capabilities') }}</UiLabel>
                  <div style="display: flex; gap: 8px; margin: 6px 0">
                    <UiInput v-model="agentCapInput" :placeholder="t('agentCenter.agentForm.capabilityPlaceholder')" @keydown.enter.prevent="addCap" style="flex: 1" />
                    <UiButton size="sm" variant="outline" @click="addCap">添加</UiButton>
                  </div>
                  <div class="model-capability-row" style="flex-wrap: wrap; gap: 4px">
                    <span v-for="(cap, i) in (agentDraft.capabilities ?? [])" :key="cap" class="provider-cap-tag" style="cursor: pointer" @click="removeCap(i)">{{ cap }} ×</span>
                  </div>
                </div>

                <!-- System Prompt -->
                <div>
                  <UiLabel>{{ t('agentCenter.agentForm.systemPrompt') }}</UiLabel>
                  <UiTextarea v-model="agentDraft.system_prompt as string" :placeholder="t('agentCenter.agentForm.systemPromptPlaceholder')" :rows="4" />
                </div>

                <div style="display: flex; align-items: center; justify-content: space-between; padding: 6px 0">
                  <UiLabel>{{ t('agentCenter.agentForm.enabled') }}</UiLabel>
                  <UiSwitch v-model="agentDraft.enabled as boolean" />
                </div>
              </div>

              <div style="display: flex; gap: 8px; justify-content: flex-end; margin-top: 14px">
                <UiButton variant="outline" size="sm" @click="showAgentForm = false">取消</UiButton>
                <UiButton size="sm" @click="saveAgentDraft">{{ t('agentCenter.agentForm.saveDraft') }}</UiButton>
              </div>
            </div>
          </UiSheet>
        </section>

        <!-- 3.2 运行图 (TOPOLOGY) -->
        <section v-if="activeSection === 'topology'" class="center-resource-section">
          <div class="center-resource-heading">
            <div>
              <h3>{{ t('agentCenter.tabs.topology') }}</h3>
              <p>双泳道拓扑编排：支持拖拽连线与运行时模式分发。</p>
            </div>
            <UiBadge variant="outline">{{ modeNodes.length }} 节点 · {{ modeEdges.length }} 边</UiBadge>
          </div>

          <div class="actions" style="display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 10px">
            <UiButton size="sm" @click="createMode"><Plus :size="14" /><span>{{ t('agentCenter.createMode') }}</span></UiButton>
            <UiButton size="sm" variant="outline" :disabled="!selectedModeId" @click="saveModeDraft">保存草稿</UiButton>
            <UiButton size="sm" :disabled="!selectedModeId" @click="publishMode">发布模式</UiButton>
            <UiButton size="sm" variant="ghost" @click="addModeNode">新增节点</UiButton>
            <UiButton size="sm" variant="outline" :disabled="!selectedModeId" @click="openVersionDrawer"><History :size="14" />版本历史</UiButton>
          </div>

          <div class="topology-workbench" style="display: grid; grid-template-columns: 240px 1fr; gap: 12px; align-items: start">
            <div class="topology-rail" style="display: grid; gap: 8px">
              <div class="model-provider-search" style="width: 100%">
                <Search :size="14" style="position: absolute; left: 10px; top: 50%; transform: translateY(-50%); color: var(--text-muted); pointer-events: none" />
                <UiInput v-model="modeQuery" placeholder="搜索模式…" style="padding-left: 28px" />
              </div>
              <div class="model-center-tabs" role="tablist" aria-label="mode-rail" style="display: grid; gap: 6px">
                <button
                  v-for="m in filteredModes"
                  :key="m.id"
                  role="tab"
                  :aria-selected="selectedModeId === m.id"
                  :class="{ active: selectedModeId === m.id }"
                  style="display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 8px 10px; border-radius: 8px; background: var(--surface-raised); border: 1px solid var(--border-muted)"
                  @click="selectMode(m.id)"
                >
                  <span style="display: grid; gap: 2px; text-align: left; min-width: 0">
                    <strong style="font-size: 12px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap">{{ m.display_name }}</strong>
                    <small style="color: var(--text-muted); font-size: 10px">rev {{ m.revision ?? '—' }}</small>
                  </span>
                  <UiBadge :variant="readinessVariant(m.status)">{{ m.status ?? 'draft' }}</UiBadge>
                </button>
                <div v-if="filteredModes.length === 0" class="quiet" style="font-size: 11px; padding: 8px">{{ t('agentCenter.empty.noModes') }}</div>
              </div>
            </div>

            <div style="display: grid; gap: 10px; min-width: 0">
              <AgentModeCanvas
                :nodes="modeNodes"
                :edges="modeEdges"
                :agents="agents"
                @update:nodes="modeNodes = $event"
                @update:edges="modeEdges = $event"
                @select-node="handleSelectNode"
                @select-edge="handleSelectEdge"
              />
              <div v-if="selectedEdge" style="display: flex; gap: 8px; align-items: center; flex-wrap: wrap; padding: 8px 10px; border: 1px solid var(--border-muted); border-radius: 10px; background: var(--surface-raised)">
                <span style="font-size: 12px">边 {{ selectedEdge.source }} → {{ selectedEdge.target }}</span>
                <UiInput v-model="edgeLabelDraft" placeholder="边标签（可选）" style="flex: 1; min-width: 160px" />
                <UiButton size="sm" variant="outline" @click="saveEdgeLabel">保存标签</UiButton>
                <UiButton size="sm" variant="ghost" class="provider-delete-btn" @click="deleteEdge"><Trash2 :size="14" />删除边</UiButton>
              </div>
            </div>
          </div>

          <!-- 节点覆写 Sheet -->
          <UiSheet :open="showNodeSheet" side="right" @update:open="showNodeSheet = $event">
            <div style="display: grid; gap: 12px; min-width: 280px; padding: 4px 0">
              <h3>节点配置 · {{ selectedModeNode?.id ?? '—' }}</h3>
              <p class="quiet" style="font-size: 11px">覆盖当前模式节点的绑定智能体与标签</p>
              <div>
                <UiLabel>绑定智能体</UiLabel>
                <select v-model="nodeOverride.agent_id" class="settings-select">
                  <option v-for="a in agents" :key="a.id" :value="a.id">{{ a.name }} ({{ normalizeLayer(a.layer) }})</option>
                </select>
              </div>
              <div>
                <UiLabel>节点标签</UiLabel>
                <UiInput v-model="nodeOverride.label" placeholder="节点显示名" />
              </div>
              <div style="display: flex; gap: 8px; justify-content: flex-end; margin-top: 10px">
                <UiButton size="sm" variant="outline" @click="showNodeSheet = false">取消</UiButton>
                <UiButton size="sm" @click="applyNodeOverride">应用</UiButton>
              </div>
            </div>
          </UiSheet>

          <!-- 拓扑版本历史 Sheet -->
          <UiSheet :open="showVersionDrawer" side="right" @update:open="showVersionDrawer = $event">
            <div style="display: grid; gap: 10px; min-width: 320px; padding: 4px 0">
              <h3>版本历史 · {{ selectedMode?.display_name ?? '—' }}</h3>
              <UiButton size="sm" variant="outline" :disabled="modeVersionsLoading" @click="loadModeVersions">刷新历史</UiButton>
              <div v-if="modeVersionsLoading" class="quiet" style="font-size: 11px">加载中…</div>
              <div v-else style="display: grid; gap: 8px; max-height: 60vh; overflow: auto">
                <div v-for="v in modeVersions" :key="v.id" style="display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 8px 10px; border: 1px solid var(--border-muted); border-radius: 8px; background: var(--surface-section)">
                  <span style="display: grid; gap: 2px">
                    <strong style="font-size: 12px">v{{ v.version }}</strong>
                    <small style="color: var(--text-muted); font-size: 10px">{{ v.created_at ? formatDate(v.created_at) : '—' }}</small>
                  </span>
                  <UiBadge :variant="v.is_active ? 'default' : 'secondary'">{{ v.is_active ? 'active' : 'archived' }}</UiBadge>
                </div>
                <div v-if="modeVersions.length === 0" class="quiet" style="font-size: 11px">暂无历史版本</div>
              </div>
            </div>
          </UiSheet>
        </section>

        <!-- 3.3 提示词引擎 (PROMPT) -->
        <section v-if="activeSection === 'prompt'" class="center-resource-section">
          <div class="center-resource-heading">
            <div>
              <h3>{{ t('agentCenter.tabs.prompt') }}</h3>
              <p>片段版本管理、A/B 效果对比、Token 预估与管线画布。</p>
            </div>
            <div style="display: flex; gap: 6px">
              <UiBadge variant="outline">{{ fragments.length }} 片段</UiBadge>
              <UiBadge variant="outline">{{ pipelines.length }} 管线</UiBadge>
            </div>
          </div>

          <div class="prompt-toolbar" style="display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 10px; align-items: center">
            <div class="model-provider-filters" role="group" aria-label="prompt-view">
              <button :class="{ active: promptCanvasMode === 'fragments' }" :aria-pressed="promptCanvasMode === 'fragments'" @click="promptCanvasMode = 'fragments'">
                <FileText :size="12" />
                <span>Fragments 片段</span>
              </button>
              <button :class="{ active: promptCanvasMode === 'canvas' }" :aria-pressed="promptCanvasMode === 'canvas'" @click="promptCanvasMode = 'canvas'">
                <Workflow :size="12" />
                <span>Pipeline 画布</span>
              </button>
            </div>
            <UiButton size="sm" variant="outline" :disabled="fragLoading" @click="loadPromptEngine">
              <RefreshCw :size="14" :class="{ 'animate-spin': fragLoading }" />
              <span>刷新</span>
            </UiButton>
            <UiButton size="sm" variant="outline" @click="openPipelineCreate">
              <Plus :size="14" />
              <span>新建管线</span>
            </UiButton>
          </div>

          <!-- Canvas 视图 -->
          <template v-if="promptCanvasMode === 'canvas'">
            <div style="display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 8px">
              <UiLabel>管线切换</UiLabel>
              <select v-model="pipelineCanvasSelectedId" class="settings-select" style="min-width: 220px">
                <option :value="null">默认管线</option>
                <option v-for="p in pipelines" :key="p.id" :value="p.id">{{ p.name ?? p.title ?? p.id.slice(0, 8) }} · {{ p.status ?? 'draft' }}</option>
              </select>
            </div>
            <PromptPipelineCanvas
              :pipeline="pipelineCanvasPipeline"
              @update:nodes="handlePipelineCanvasUpdateNodes"
              @update:edges="handlePipelineCanvasUpdateEdges"
              @select-node="pipelineCanvasSelectedNode = $event"
              @select-edge="pipelineCanvasSelectedEdge = $event"
            />
            <div class="card-grid" style="display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 10px; margin-top: 10px">
              <UiCard v-for="p in pipelines" :key="p.id">
                <template #content>
                  <div style="font-weight: 600; display: flex; gap: 6px; align-items: center; flex-wrap: wrap">
                    {{ p.name ?? p.title ?? p.id.slice(0, 8) }}
                    <UiBadge variant="outline">{{ p.status ?? 'draft' }}</UiBadge>
                  </div>
                  <div class="quiet" style="font-size: 12px; margin: 4px 0">{{ p.description ?? '—' }}</div>
                  <div style="display: flex; gap: 6px; margin-top: 8px">
                    <UiButton size="xs" variant="outline" @click="openPipelineEdit(p)">编辑</UiButton>
                    <UiButton size="xs" @click="publishPipeline(p.id)">发布</UiButton>
                    <UiButton size="xs" variant="ghost" @click="pipelineCanvasSelectedId = p.id">查看画布</UiButton>
                  </div>
                </template>
              </UiCard>
            </div>
          </template>

          <!-- Fragments 引擎视图 -->
          <template v-else>
            <div class="model-provider-toolbar" style="margin-bottom: 12px">
              <div class="model-provider-search">
                <Search :size="15" />
                <UiInput v-model="fragQuery" placeholder="搜索提示词片段标题、key 或分类…" />
              </div>
              <div class="model-provider-filters" role="group" aria-label="enabled">
                <button :class="{ active: fragEnabledFilter === 'all' }" :aria-pressed="fragEnabledFilter === 'all'" @click="fragEnabledFilter = 'all'">全部</button>
                <button :class="{ active: fragEnabledFilter === 'enabled' }" :aria-pressed="fragEnabledFilter === 'enabled'" @click="fragEnabledFilter = 'enabled'">启用</button>
                <button :class="{ active: fragEnabledFilter === 'disabled' }" :aria-pressed="fragEnabledFilter === 'disabled'" @click="fragEnabledFilter = 'disabled'">停用</button>
              </div>
              <span class="model-provider-count">显示 {{ filteredFragments.length }} / {{ fragments.length }}</span>
            </div>

            <div class="pe-layout">
              <div class="pe-fragment-list">
                <div class="pe-list-header">
                  <h3>Fragments</h3>
                  <UiBadge variant="outline">{{ filteredFragments.length }}</UiBadge>
                </div>
                <div v-if="filteredFragments.length === 0" class="quiet" style="font-size: 12px; padding: 12px">{{ t('agentCenter.empty.noPrompts') }}</div>
                <button
                  v-for="fragment in filteredFragments"
                  :key="fragment.id"
                  class="pe-fragment-card"
                  :class="{ active: selectedFragmentId === fragment.id }"
                  @click="selectFragment(fragment)"
                >
                  <div class="pe-fragment-head">
                    <strong :title="fragment.title">{{ fragment.title }}</strong>
                    <UiBadge v-if="fragment.is_builtin" variant="secondary">builtin</UiBadge>
                  </div>
                  <span class="pe-fragment-meta">{{ fragment.scope }} / {{ fragment.category }} · prio {{ fragment.priority }}</span>
                </button>
              </div>

              <div class="pe-detail">
                <template v-if="selectedFragment">
                  <UiCard class="pe-detail-card">
                    <template #content>
                      <div class="pe-detail-head">
                        <div>
                          <h3>{{ selectedFragment.title }}</h3>
                          <p>{{ selectedFragment.key }} · {{ selectedFragment.scope }} / {{ selectedFragment.category }}</p>
                        </div>
                        <UiBadge :variant="selectedFragment.enabled ? 'default' : 'secondary'">{{ selectedFragment.enabled ? 'enabled' : 'disabled' }}</UiBadge>
                      </div>

                      <div v-if="fragmentEffectiveness" class="pe-metrics">
                        <div class="pe-metric"><Activity :size="14" /><div><span>Effectiveness</span><strong>{{ (fragmentEffectiveness.effectiveness_score * 100).toFixed(0) }}%</strong></div></div>
                        <div class="pe-metric"><History :size="14" /><div><span>Active Version</span><strong>v{{ fragmentEffectiveness.active_version }}</strong></div></div>
                        <div class="pe-metric"><ThumbsUp :size="14" /><div><span>Positive</span><strong>{{ fragmentEffectiveness.positive_signals }}</strong></div></div>
                        <div class="pe-metric"><ThumbsDown :size="14" /><div><span>Negative</span><strong>{{ fragmentEffectiveness.negative_signals }}</strong></div></div>
                      </div>

                      <div class="pe-section">
                        <div class="pe-section-head">
                          <div class="pe-section-title"><GitBranch :size="14" /><span>Current Content</span></div>
                          <UiButton size="sm" variant="outline" :disabled="Boolean(selectedFragment.is_builtin)" @click="openNewVersion"><Plus :size="14" /><span>New Version</span></UiButton>
                        </div>
                        <textarea :value="selectedFragment.content" class="pe-content-textarea" rows="6" readonly></textarea>
                      </div>
                    </template>
                  </UiCard>

                  <UiCard class="pe-detail-card">
                    <template #content>
                      <div class="pe-section-head">
                        <div class="pe-section-title"><History :size="14" /><span>Version History</span></div>
                        <UiBadge variant="outline">{{ fragmentVersions.length }}</UiBadge>
                      </div>
                      <div v-if="fragmentVersions.length === 0" class="quiet" style="font-size: 12px">暂无版本历史</div>
                      <div v-else class="pe-version-list">
                        <div v-for="version in sortedVersions" :key="version.id" class="pe-version-row" :class="{ active: version.is_active }">
                          <div class="pe-version-main">
                            <div class="pe-version-head">
                              <strong>v{{ version.version }}</strong>
                              <UiBadge v-if="version.is_active" variant="default">active</UiBadge>
                              <span class="pe-version-date">{{ formatDate(version.created_at) }}</span>
                            </div>
                            <p class="pe-version-summary">{{ version.change_summary }}</p>
                          </div>
                          <div class="pe-version-actions">
                            <UiButton v-if="!version.is_active" size="sm" variant="ghost" :disabled="fragBusy" @click="rollbackVersion(version.version)"><Undo2 :size="14" /><span>回滚</span></UiButton>
                          </div>
                        </div>
                      </div>
                    </template>
                  </UiCard>
                </template>
              </div>
            </div>

            <!-- 上下文预览区 -->
            <UiCard class="pe-preview-card" style="margin-top: 12px">
              <template #content>
                <div class="pe-section-head">
                  <div class="pe-section-title"><Eye :size="14" /><span>上下文预览与 Token 预估</span></div>
                  <UiBadge v-if="promptPreview" variant="outline">{{ promptPreview.estimated_tokens }} tokens</UiBadge>
                </div>
                <div style="display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 8px; margin-bottom: 10px">
                  <div><UiLabel>智能体 ID</UiLabel><UiInput v-model="promptPreviewAgentId" placeholder="agent_meeting" /></div>
                  <div><UiLabel>运行模式</UiLabel><UiInput v-model="promptPreviewMode" placeholder="plan-first" /></div>
                  <div><UiLabel>会话 ID (可选)</UiLabel><UiInput v-model="promptPreviewSessionId" placeholder="session_id" /></div>
                </div>
                <UiButton size="sm" :disabled="previewBusy" @click="generatePromptPreview"><Eye :size="14" /><span>生成预览</span></UiButton>
                <div v-if="promptPreview" style="margin-top: 10px">
                  <textarea :value="promptPreview.system_prompt" class="pe-content-textarea" rows="6" readonly></textarea>
                </div>
              </template>
            </UiCard>
          </template>
        </section>

        <!-- 3.4 智能体演化 (EVOLUTION) -->
        <section v-if="activeSection === 'evolution'" class="center-resource-section evolution-section">
          <div class="center-resource-heading">
            <div>
              <h3>{{ t('agentCenter.tabs.evolution') }}</h3>
              <p>从历史会话中提炼模式，生成特化智能体候选提案并一键晋升。</p>
            </div>
            <UiBadge variant="outline">{{ proposals.length }} 提案</UiBadge>
          </div>

          <UiCard class="evolution-generate-card">
            <template #content>
              <div class="evolution-generate-row">
                <div class="evolution-generate-field">
                  <UiLabel>{{ t('agentCenter.evolution.targetSession') }}</UiLabel>
                  <UiInput v-model="generateSessionId" placeholder="输入已结束的会话 ID…" />
                </div>
                <div class="evolution-generate-field">
                  <UiLabel>回溯事件数量</UiLabel>
                  <UiInput v-model="generateLookback" type="number" placeholder="200" />
                </div>
                <UiButton :disabled="evolutionBusy" @click="generateProposals">
                  <Sparkles :size="14" />
                  <span>{{ t('agentCenter.evolution.generateAction') }}</span>
                </UiButton>
              </div>
            </template>
          </UiCard>

          <div class="evolution-list-header">
            <h3>{{ t('agentCenter.evolution.proposalsList') }}</h3>
            <UiBadge variant="outline">{{ proposals.length }}</UiBadge>
          </div>

          <div v-if="proposals.length === 0" class="model-provider-empty">
            <Dna :size="24" />
            <span>{{ t('agentCenter.evolution.noProposals') }}</span>
          </div>

          <div v-else class="evolution-proposal-grid">
            <button
              v-for="proposal in sortedProposals"
              :key="proposal.id"
              class="evolution-proposal-card"
              :class="{ active: selectedProposalId === proposal.id }"
              @click="selectedProposalId = proposal.id"
            >
              <div class="evolution-proposal-head">
                <span class="evolution-proposal-icon" :class="normalizeLayer(proposal.layer)">
                  <component :is="normalizeLayer(proposal.layer) === 'execution' ? Cpu : Workflow" :size="16" />
                </span>
                <span class="evolution-proposal-main">
                  <strong :title="proposal.name">{{ proposal.name }}</strong>
                  <small>{{ normalizeLayer(proposal.layer) }} · {{ proposal.agent_type }}</small>
                </span>
                <UiBadge :variant="confidenceVariant(proposal.confidence_score)">{{ (proposal.confidence_score * 100).toFixed(0) }}%</UiBadge>
              </div>
              <p class="evolution-proposal-desc">{{ proposal.description || '—' }}</p>
              <div class="evolution-proposal-meta">
                <UiBadge :variant="evolutionStatusVariant(proposal.status)">{{ evolutionStatusLabel(proposal.status) }}</UiBadge>
                <span class="evolution-proposal-by">by {{ proposal.generated_by_agent_id }}</span>
              </div>
            </button>
          </div>

          <!-- 选中提案详情面板 -->
          <UiCard v-if="selectedProposal" class="evolution-detail-panel" style="margin-top: 14px">
            <template #content>
              <div class="evolution-detail-head">
                <span class="evolution-proposal-icon" :class="normalizeLayer(selectedProposal.layer)">
                  <component :is="normalizeLayer(selectedProposal.layer) === 'execution' ? Cpu : Workflow" :size="20" />
                </span>
                <div style="flex: 1; min-width: 0">
                  <h3 style="margin: 0; font-size: 15px">{{ selectedProposal.name }}</h3>
                  <p style="margin: 2px 0 0; font-size: 11px; color: var(--text-muted)">
                    {{ normalizeLayer(selectedProposal.layer) }} · {{ selectedProposal.agent_type }} · {{ evolutionStatusLabel(selectedProposal.status) }}
                  </p>
                </div>
                <UiBadge :variant="confidenceVariant(selectedProposal.confidence_score)">置信度 {{ (selectedProposal.confidence_score * 100).toFixed(0) }}%</UiBadge>
              </div>

              <div class="evolution-detail-section">
                <div class="evolution-detail-section-title">{{ t('agentCenter.evolution.patterns') }}</div>
                <ul class="evolution-pattern-list">
                  <li v-for="pat in (selectedProposal.observed_patterns ?? [])" :key="pat">{{ pat }}</li>
                  <li v-if="(selectedProposal.observed_patterns ?? []).length === 0" class="quiet">—</li>
                </ul>
              </div>

              <div class="evolution-detail-section">
                <div class="evolution-detail-section-title">{{ t('agentCenter.evolution.suggestedTools') }}</div>
                <div class="evolution-tag-row">
                  <span v-for="tool in (selectedProposal.suggested_tools ?? [])" :key="tool" class="provider-cap-tag">{{ tool }}</span>
                  <span v-if="(selectedProposal.suggested_tools ?? []).length === 0" class="quiet">—</span>
                </div>
              </div>

              <div v-if="selectedProposal.status === 'proposed' || selectedProposal.status === 'evaluating'" class="evolution-detail-actions">
                <UiButton :disabled="evolutionBusy" @click="openPromoteModal(selectedProposal!)">
                  <Check :size="14" />
                  <span>{{ t('agentCenter.evolution.promote') }}</span>
                </UiButton>
                <UiInput v-model="rejectReason" placeholder="拒绝原因（可选）…" style="flex: 1; min-width: 160px" />
                <UiButton variant="ghost" :disabled="evolutionBusy" @click="rejectEvolutionCandidate(selectedProposal!)">
                  <ThumbsDown :size="14" />
                  <span>{{ t('agentCenter.evolution.reject') }}</span>
                </UiButton>
              </div>
            </template>
          </UiCard>

          <!-- 晋升抽屉 Sheet (UiSheet) -->
          <UiSheet :open="showPromoteSheet" side="right" @update:open="showPromoteSheet = $event">
            <div style="display: grid; gap: 14px; min-width: 360px; padding: 4px 0">
              <h3>{{ t('agentCenter.evolution.promoteTitle') }}</h3>
              <p class="quiet" style="font-size: 12px">晋升 <strong>{{ selectedProposal?.name }}</strong> 为正式智能体定义</p>

              <div style="display: grid; gap: 10px">
                <div>
                  <UiLabel>智能体 ID</UiLabel>
                  <UiInput v-model="promoteForm.agent_id" placeholder="agent_xxx" />
                </div>
                <div>
                  <UiLabel>架构模式</UiLabel>
                  <UiInput v-model="promoteForm.mode" placeholder="plan / execute" />
                </div>
                <div>
                  <UiLabel>模型路由目的</UiLabel>
                  <UiInput v-model="promoteForm.model_route_purpose" placeholder="chat / planner" />
                </div>

                <div>
                  <UiLabel>授权工具</UiLabel>
                  <div style="display: flex; gap: 8px; margin: 6px 0">
                    <UiInput v-model="promoteToolInput" placeholder="tool id" @keydown.enter.prevent="addPromoteTool" style="flex: 1" />
                    <UiButton size="sm" variant="outline" @click="addPromoteTool">添加</UiButton>
                  </div>
                  <div class="model-capability-row" style="flex-wrap: wrap; gap: 4px">
                    <span v-for="tool in promoteForm.allowed_tools" :key="tool" class="provider-cap-tag" style="cursor: pointer" @click="removePromoteTool(tool)">{{ tool }} ×</span>
                  </div>
                </div>

                <div>
                  <UiLabel>能力标签</UiLabel>
                  <div style="display: flex; gap: 8px; margin: 6px 0">
                    <UiInput v-model="promoteCapabilityInput" placeholder="capability" @keydown.enter.prevent="addPromoteCap" style="flex: 1" />
                    <UiButton size="sm" variant="outline" @click="addPromoteCap">添加</UiButton>
                  </div>
                  <div class="model-capability-row" style="flex-wrap: wrap; gap: 4px">
                    <span v-for="cap in promoteForm.capabilities" :key="cap" class="provider-cap-tag" style="cursor: pointer" @click="removePromoteCap(cap)">{{ cap }} ×</span>
                  </div>
                </div>
              </div>

              <div style="display: flex; gap: 8px; justify-content: flex-end; margin-top: 14px">
                <UiButton variant="outline" size="sm" @click="showPromoteSheet = false">取消</UiButton>
                <UiButton size="sm" :disabled="evolutionBusy || !promoteForm.agent_id.trim()" @click="submitPromote">确认晋升</UiButton>
              </div>
            </div>
          </UiSheet>
        </section>

        <!-- 3.5 智能体信息与实例 (INFO) -->
        <section v-if="activeSection === 'info'" class="center-resource-section">
          <div class="center-resource-heading">
            <div>
              <h3>{{ t('agentCenter.tabs.info') }}</h3>
              <p>监控当前会话与任务中各智能体实例的运行状态与改派控制。</p>
            </div>
            <UiBadge variant="outline">{{ runtimeInstances.length }} 实例</UiBadge>
          </div>

          <!-- 工具栏 -->
          <div class="model-provider-toolbar">
            <div class="model-provider-search">
              <Search :size="15" />
              <UiInput v-model="runtimeSessionFilter" :placeholder="t('agentCenter.filter.searchInstances')" />
            </div>
            <div class="model-provider-filters" role="group" aria-label="runtime-status">
              <button :class="{ active: runtimeStatusFilter === 'all' }" :aria-pressed="runtimeStatusFilter === 'all'" @click="runtimeStatusFilter = 'all'">全部</button>
              <button :class="{ active: runtimeStatusFilter === 'running' }" :aria-pressed="runtimeStatusFilter === 'running'" @click="runtimeStatusFilter = 'running'">运行中</button>
              <button :class="{ active: runtimeStatusFilter === 'completed' }" :aria-pressed="runtimeStatusFilter === 'completed'" @click="runtimeStatusFilter = 'completed'">已完成</button>
            </div>
            <UiButton size="sm" variant="outline" @click="loadRuntimeInstances">查询</UiButton>
            <UiButton size="sm" @click="startRuntimePoll">实时轮询</UiButton>
            <UiButton size="sm" variant="ghost" @click="stopRuntimePoll">停止</UiButton>
          </div>

          <div v-if="filteredRuntimeInstances.length === 0" class="model-provider-empty">
            <Info :size="24" />
            <span>{{ t('agentCenter.runtimeInfo.noInstances') }}</span>
          </div>

          <div v-else style="display: grid; gap: 8px; margin-top: 10px">
            <div
              v-for="inst in pagedRuntimeInstances"
              :key="inst.id"
              style="border: 1px solid var(--border-muted); border-radius: 10px; padding: 12px; display: grid; gap: 6px; background: var(--surface-section)"
            >
              <div style="display: flex; gap: 6px; align-items: center; flex-wrap: wrap">
                <strong>{{ inst.agent_name ?? inst.agent_id ?? inst.id.slice(0, 8) }}</strong>
                <UiBadge :variant="readinessVariant(inst.status)">{{ inst.status }}</UiBadge>
                <span class="quiet" style="font-size: 11px">run: {{ inst.run_id.slice(0, 8) }} · session: {{ String(inst.session_id ?? '—').slice(0, 8) }}</span>
              </div>
              <div style="display: flex; gap: 8px; flex-wrap: wrap; margin-top: 4px">
                <UiButton size="xs" variant="outline" :disabled="runtimeControlBusy === `${inst.run_id}:pause`" @click="controlInstance(inst.run_id, 'pause')">
                  {{ t('agentCenter.runtimeInfo.actionPause') }}
                </UiButton>
                <UiButton size="xs" variant="outline" :disabled="runtimeControlBusy === `${inst.run_id}:resume`" @click="controlInstance(inst.run_id, 'resume')">
                  {{ t('agentCenter.runtimeInfo.actionResume') }}
                </UiButton>
                <UiButton size="xs" variant="destructive" :disabled="runtimeControlBusy === `${inst.run_id}:cancel`" @click="controlInstance(inst.run_id, 'cancel')">
                  {{ t('agentCenter.runtimeInfo.actionCancel') }}
                </UiButton>
                <UiButton size="xs" variant="ghost" @click="openReassignForm(inst.run_id)">{{ t('agentCenter.runtimeInfo.actionSteer') }}</UiButton>
              </div>
            </div>

            <!-- 分页 -->
            <div style="display: flex; gap: 8px; align-items: center; justify-content: center; margin-top: 8px">
              <UiButton size="xs" variant="outline" :disabled="runtimePage <= 1" @click="runtimePage = Math.max(1, runtimePage - 1)">上一页</UiButton>
              <span class="quiet" style="font-size: 11px">{{ runtimePage }} / {{ runtimeTotalPages }}</span>
              <UiButton size="xs" variant="outline" :disabled="runtimePage >= runtimeTotalPages" @click="runtimePage = Math.min(runtimeTotalPages, runtimePage + 1)">下一页</UiButton>
            </div>
          </div>

          <!-- 改派抽屉 Sheet (UiSheet) -->
          <UiSheet :open="showReassignSheet" side="right" @update:open="showReassignSheet = $event">
            <div style="display: grid; gap: 12px; min-width: 320px; padding: 4px 0">
              <h3>队列交互改派</h3>
              <p class="quiet" style="font-size: 11px">将排队或执行中的消息改派至目标 Run</p>
              <div>
                <UiLabel>Session ID</UiLabel>
                <UiInput v-model="reassignForm.session_id" placeholder="session_id" />
              </div>
              <div>
                <UiLabel>Interaction ID</UiLabel>
                <UiInput v-model="reassignForm.interaction_id" placeholder="interaction_id" />
              </div>
              <div>
                <UiLabel>Target Run ID</UiLabel>
                <UiInput v-model="reassignForm.target_run_id" placeholder="target_run_id" />
              </div>
              <div style="display: flex; gap: 8px; justify-content: flex-end; margin-top: 10px">
                <UiButton size="sm" variant="outline" @click="showReassignSheet = false">取消</UiButton>
                <UiButton size="sm" @click="submitReassign">确认改派</UiButton>
              </div>
            </div>
          </UiSheet>
        </section>
      </main>
    </div>
  </div>
</template>

<style scoped>
.agent-center-page {
  display: grid;
  gap: 12px;
  padding: 16px;
  max-width: 1280px;
  margin: 0 auto;
}
.quiet {
  color: var(--text-muted);
}
.provider-cap-tag {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 8px;
  border-radius: 6px;
  font-size: 11px;
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
  color: var(--text-primary);
}

/* Prompt Engineering Layout */
.pe-layout {
  display: grid;
  grid-template-columns: 280px 1fr;
  gap: 14px;
  align-items: start;
}
.pe-fragment-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
  max-height: 580px;
  overflow-y: auto;
  padding-right: 4px;
}
.pe-list-header {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
}
.pe-list-header h3 {
  margin: 0;
  font-size: 13px;
}
.pe-fragment-card {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 10px 12px;
  background: var(--surface-section);
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  cursor: pointer;
  text-align: left;
  color: inherit;
  transition: border-color 0.15s;
}
.pe-fragment-card:hover {
  border-color: var(--accent-primary);
}
.pe-fragment-card.active {
  border-color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 8%, var(--surface-section));
}
.pe-fragment-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
}
.pe-fragment-head strong {
  font-size: 13px;
}
.pe-fragment-meta {
  font-size: 11px;
  color: var(--text-muted);
}
.pe-detail {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.pe-detail-head {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 12px;
  margin-bottom: 12px;
}
.pe-detail-head h3 {
  margin: 0;
  font-size: 15px;
}
.pe-detail-head p {
  margin: 2px 0 0;
  font-size: 12px;
  color: var(--text-muted);
}
.pe-metrics {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(130px, 1fr));
  gap: 8px;
  margin-bottom: 12px;
}
.pe-metric {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
}
.pe-metric span {
  font-size: 11px;
  color: var(--text-muted);
  display: block;
}
.pe-metric strong {
  font-size: 14px;
}
.pe-section {
  margin-top: 10px;
}
.pe-section-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 6px;
}
.pe-section-title {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-muted);
  text-transform: uppercase;
}
.pe-content-textarea {
  width: 100%;
  padding: 8px 10px;
  background: var(--surface-input);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  color: inherit;
  font-size: 12px;
  font-family: var(--font-mono, monospace);
  resize: vertical;
  line-height: 1.5;
}
.pe-version-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.pe-version-row {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  padding: 8px 10px;
  background: var(--surface-section);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
}
.pe-version-row.active {
  border-color: var(--accent-success);
  background: color-mix(in srgb, var(--accent-success) 8%, var(--surface-section));
}
.pe-version-head {
  display: flex;
  align-items: center;
  gap: 8px;
}
.pe-version-date {
  font-size: 11px;
  color: var(--text-muted);
}
.pe-version-summary {
  margin: 2px 0 0;
  font-size: 12px;
  color: var(--text-muted);
}

/* Evolution Styles */
.evolution-generate-card :deep(.ui-card-content) {
  padding: 14px 16px;
}
.evolution-generate-row {
  display: flex;
  gap: 12px;
  align-items: flex-end;
  flex-wrap: wrap;
}
.evolution-generate-field {
  flex: 1 1 200px;
  min-width: 180px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.evolution-list-header {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 14px 0 8px;
}
.evolution-list-header h3 {
  margin: 0;
  font-size: 14px;
}
.evolution-proposal-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 10px;
}
.evolution-proposal-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px 14px;
  background: var(--surface-section);
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  cursor: pointer;
  text-align: left;
  color: inherit;
  transition: border-color 0.15s, background 0.15s;
}
.evolution-proposal-card:hover {
  border-color: var(--accent-primary);
}
.evolution-proposal-card.active {
  border-color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 8%, var(--surface-section));
}
.evolution-proposal-head {
  display: flex;
  align-items: center;
  gap: 10px;
}
.evolution-proposal-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: 6px;
  background: color-mix(in srgb, var(--accent-primary) 14%, transparent);
  color: var(--accent-primary);
  flex-shrink: 0;
}
.evolution-proposal-icon.execution {
  background: color-mix(in srgb, var(--accent-success) 14%, transparent);
  color: var(--accent-success);
}
.evolution-proposal-main {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.evolution-proposal-main strong {
  font-size: 13px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.evolution-proposal-main small {
  font-size: 11px;
  color: var(--text-muted);
}
.evolution-proposal-desc {
  margin: 0;
  font-size: 12px;
  color: var(--text-muted);
  line-height: 1.4;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}
.evolution-proposal-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 11px;
}
.evolution-proposal-by {
  color: var(--text-muted);
}
.evolution-detail-panel :deep(.ui-card-content) {
  padding: 16px 18px;
}
.evolution-detail-head {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 12px;
}
.evolution-detail-section {
  margin-top: 10px;
}
.evolution-detail-section-title {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-muted);
  text-transform: uppercase;
  margin-bottom: 6px;
}
.evolution-pattern-list {
  margin: 0;
  padding-left: 18px;
  font-size: 12px;
  line-height: 1.6;
}
.evolution-tag-row {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.evolution-detail-actions {
  display: flex;
  gap: 8px;
  align-items: center;
  margin-top: 16px;
  padding-top: 12px;
  border-top: 1px solid var(--border-muted);
  flex-wrap: wrap;
}

@media (max-width: 900px) {
  .pe-layout {
    grid-template-columns: 1fr;
  }
}
</style>
