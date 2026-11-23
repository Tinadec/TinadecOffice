<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { routerKey } from 'vue-router'
import {
  ArrowUpRight,
  Bot,
  Brain,
  CheckCircle2,
  Database,
  FileCode2,
  FolderGit2,
  RefreshCw,
  RotateCcw,
  Save,
  Sliders,
  Sparkles,
  ThumbsDown,
  UserRound,
} from '@lucide/vue'
import {
  api as baseApi,
  type ContextEvolutionProposalDto,
  type ProjectDto,
} from '@/api'
import ModeContextStrategySection from '@/components/modeEditor/ModeContextStrategySection.vue'
import { UiBadge, UiButton, UiInput, UiLabel, UiSelectField, UiSwitch, UiTextarea } from '@/components/ui'
import { triggerDiagnoseInComposer } from '@/lib/offlineDiagnosticReport'
import { useNotifications } from '@/composables/useNotifications'
import { companionApi, type CompanionSummary } from '@/lib/companionApi'
import {
  COMPANION_MEMORY_SECTIONS,
  contextMemoryApi,
  DEFAULT_CONTEXT_POLICY_PARAMS,
  estimateMarkdownTokens,
  extractMarkdownMemoryAnchors,
  parseCompanionStructuredMarkdown,
  serializeCompanionStructuredMarkdown,
  type CompanionMemorySectionKey,
  type ContextMemoryPolicyDto,
  type ContextPolicyParams,
  type ContextStrategyTier,
  type MemoryDocumentDto,
  type MemoryDocumentScope,
  type MemoryKind,
  type MemoryLedgerItemDto,
} from '@/lib/contextMemoryApi'
import { useHostAccess } from '@/lib/hostAccess'
import { requestConversation, requestSettingsSection } from '@/lib/pageRequests'
import { scopedApi } from '@/lib/storageScope'
import { patchAgentCenterUi } from '@/settings/drafts/uiState'
import MemoryScopeLedgerPanel from '@/settings/memoryContext/MemoryScopeLedgerPanel.vue'

export type MemoryContextTabKey = 'principal' | 'project' | 'companion' | 'policy' | 'evolution'
type ScopeViewMode = 'split' | 'markdown' | 'ledger'
type ProposalFilterStatus = 'pending' | 'applied' | 'rejected' | 'all'

const props = withDefaults(
  defineProps<{
    initialTab?: MemoryContextTabKey
  }>(),
  {
    initialTab: 'principal',
  },
)

const emit = defineEmits<{
  'open-runtime-toml': []
}>()

const api = scopedApi(baseApi, () => 'user')
const { t } = useI18n()
const { canAccessBackend, reason: hostReason } = useHostAccess()
const { notify, confirm } = useNotifications()

const router = inject(routerKey, null)

const activeTab = ref<MemoryContextTabKey>(props.initialTab)
const viewMode = ref<ScopeViewMode>('split')
const companionEditorMode = ref<'structured' | 'raw'>('structured')

const loading = ref(false)
const loadError = ref<string | null>(null)
const savingDoc = ref(false)
const savingPolicy = ref(false)
const addingItem = ref(false)
const busyItemId = ref<string | null>(null)

const projects = ref<ProjectDto[]>([])
const selectedProjectId = ref('')
const companions = ref<CompanionSummary[]>([])
const selectedCompanionId = ref('')

const documents = reactive<Record<MemoryDocumentScope, MemoryDocumentDto | null>>({
  principal: null,
  project: null,
  companion: null,
})

const markdownDrafts = reactive<Record<MemoryDocumentScope, string>>({
  principal: '',
  project: '',
  companion: '',
})

const companionSections = reactive<Record<CompanionMemorySectionKey, string>>({
  persona_bond: '',
  episodic_highlights: '',
  operational_focus: '',
  cross_project_notes: '',
})
const companionPreamble = ref('')
const companionExtras = ref<Array<{ heading: string; body: string }>>([])

const ledgerItems = reactive<Record<MemoryDocumentScope, MemoryLedgerItemDto[]>>({
  principal: [],
  project: [],
  companion: [],
})

const policy = ref<ContextMemoryPolicyDto | null>(null)
const policyStrategyDraft = ref<ContextStrategyTier>('managed')
const policyAdvancedDraft = ref<Partial<ContextPolicyParams>>({ ...DEFAULT_CONTEXT_POLICY_PARAMS })
const companionCanWriteProjectDraft = ref(false)

const proposals = ref<ContextEvolutionProposalDto[]>([])
const pendingProposalCount = ref(0)
const autoTuneLowRiskSwitch = ref(false)
const proposalFilter = ref<ProposalFilterStatus>('pending')
const proposalsLoading = ref(false)
const busyProposalId = ref<string | null>(null)
const proposalReasonDrafts = reactive<Record<string, string>>({})
const appliedTomlPreviews = reactive<Record<string, string>>({})
const appliedSummaries = reactive<Record<string, string>>({})

let readGeneration = 0

const tabs = computed(() => [
  { key: 'principal' as const, icon: UserRound, label: t('memoryContext.tabs.principal'), badgeCount: 0 },
  { key: 'project' as const, icon: FolderGit2, label: t('memoryContext.tabs.project'), badgeCount: 0 },
  { key: 'companion' as const, icon: Bot, label: t('memoryContext.tabs.companion'), badgeCount: 0 },
  { key: 'policy' as const, icon: Sliders, label: t('memoryContext.tabs.policy'), badgeCount: 0 },
  {
    key: 'evolution' as const,
    icon: Sparkles,
    label: t('memoryContext.tabs.evolution'),
    badgeCount: pendingProposalCount.value,
  },
])

const selectedProject = computed(
  () => projects.value.find((item) => item.id === selectedProjectId.value) ?? null,
)

const projectOptions = computed(() =>
  projects.value.map((project) => ({
    value: project.id,
    label: `${project.name} (${project.path})`,
  })),
)

const companionOptions = computed(() =>
  companions.value.map((comp) => ({
    value: comp.id,
    label: comp.name || comp.id,
  })),
)

function syncCompanionStructuredFromRaw(raw: string) {
  const parsed = parseCompanionStructuredMarkdown(raw)
  companionPreamble.value = parsed.preamble
  companionExtras.value = parsed.extras
  for (const spec of COMPANION_MEMORY_SECTIONS) {
    companionSections[spec.key] = parsed.sections[spec.key]
  }
}

function syncCompanionRawFromStructured() {
  markdownDrafts.companion = serializeCompanionStructuredMarkdown({
    preamble: companionPreamble.value,
    sections: { ...companionSections },
    extras: companionExtras.value,
  })
}

function onCompanionSectionInput(key: CompanionMemorySectionKey, value: string) {
  companionSections[key] = value
  syncCompanionRawFromStructured()
}

function onCompanionRawInput(value: string) {
  markdownDrafts.companion = value
  syncCompanionStructuredFromRaw(value)
}

function isScopeDirty(scope: MemoryDocumentScope): boolean {
  const doc = documents[scope]
  if (!doc) return false
  return markdownDrafts[scope] !== doc.markdown
}

const isPolicyDirty = computed(() => {
  const base = policy.value
  if (!base) return false
  const adv = policyAdvancedDraft.value
  return (
    policyStrategyDraft.value !== base.strategy ||
    (adv.recall_fidelity ?? base.recall_fidelity) !== base.recall_fidelity ||
    (adv.compaction_trigger_ratio ?? base.compaction_trigger_ratio) !== base.compaction_trigger_ratio ||
    (adv.recent_message_limit ?? base.recent_message_limit) !== base.recent_message_limit ||
    (adv.allow_cross_session_recall ?? base.allow_cross_session_recall) !== base.allow_cross_session_recall ||
    (adv.evolution_mode ?? base.evolution_mode) !== base.evolution_mode ||
    (adv.core_markdown_max_tokens ?? base.core_markdown_max_tokens) !== base.core_markdown_max_tokens ||
    companionCanWriteProjectDraft.value !== base.companion_can_write_project_memory
  )
})

const hasAnyUnsavedChanges = computed(
  () => isScopeDirty('principal') || isScopeDirty('project') || isScopeDirty('companion') || isPolicyDirty.value,
)

function scopeTokenUsage(scope: MemoryDocumentScope) {
  const doc = documents[scope]
  const draft = markdownDrafts[scope]
  const max =
    doc?.core_markdown_max_tokens ??
    policy.value?.core_markdown_max_tokens ??
    DEFAULT_CONTEXT_POLICY_PARAMS.core_markdown_max_tokens
  const used =
    doc && draft === doc.markdown ? doc.estimated_tokens : estimateMarkdownTokens(draft)
  const ratio = max > 0 ? Math.min(1, used / max) : 0
  const items = ledgerItems[scope]
  const hotCount =
    doc?.active_item_count ??
    items.filter((i) => i.in_core_markdown && i.status === 'active').length
  const coldCount =
    doc?.cold_archive_count ??
    items.filter((i) => !i.in_core_markdown && i.status !== 'revoked').length
  return {
    used,
    max,
    ratio,
    percent: Math.min(100, Math.round(ratio * 100)),
    hotCount,
    coldCount,
    anchorsCount: extractMarkdownMemoryAnchors(draft).length,
  }
}

const projectBudget = computed(() => scopeTokenUsage('project'))
const principalBudget = computed(() => scopeTokenUsage('principal'))
const companionBudget = computed(() => scopeTokenUsage('companion'))

const tomlPreviewText = computed(() => {
  const strat = policyStrategyDraft.value
  const adv = {
    recall_fidelity: policyAdvancedDraft.value.recall_fidelity ?? DEFAULT_CONTEXT_POLICY_PARAMS.recall_fidelity,
    compaction_trigger_ratio:
      policyAdvancedDraft.value.compaction_trigger_ratio ?? DEFAULT_CONTEXT_POLICY_PARAMS.compaction_trigger_ratio,
    recent_message_limit:
      policyAdvancedDraft.value.recent_message_limit ?? DEFAULT_CONTEXT_POLICY_PARAMS.recent_message_limit,
    allow_cross_session_recall:
      policyAdvancedDraft.value.allow_cross_session_recall ?? DEFAULT_CONTEXT_POLICY_PARAMS.allow_cross_session_recall,
    evolution_mode: policyAdvancedDraft.value.evolution_mode ?? DEFAULT_CONTEXT_POLICY_PARAMS.evolution_mode,
    core_markdown_max_tokens:
      policyAdvancedDraft.value.core_markdown_max_tokens ?? DEFAULT_CONTEXT_POLICY_PARAMS.core_markdown_max_tokens,
  }
  return [
    '[context]',
    `strategy = "${strat}" # "direct" | "managed" | "deep_curator"`,
    `recent_message_limit = ${adv.recent_message_limit}`,
    `compaction_trigger_ratio = ${adv.compaction_trigger_ratio.toFixed(2)}`,
    `recall_fidelity = "${adv.recall_fidelity}" # "summary_only" | "balanced" | "verbatim_first"`,
    `allow_cross_session_recall = ${adv.allow_cross_session_recall}`,
    `auto_tune_low_risk_context_policy = ${autoTuneLowRiskSwitch.value} # conservative default: human review queue`,
    '',
    '[memory]',
    `evolution_mode = "${adv.evolution_mode}" # "auto_evolve" | "candidate_only"`,
    `core_markdown_max_tokens = ${adv.core_markdown_max_tokens}`,
    `companion_can_write_project_memory = ${companionCanWriteProjectDraft.value}`,
  ].join('\n')
})

function applyLoadedPolicy(loaded: ContextMemoryPolicyDto) {
  policy.value = loaded
  policyStrategyDraft.value = loaded.strategy
  policyAdvancedDraft.value = {
    recall_fidelity: loaded.recall_fidelity,
    compaction_trigger_ratio: loaded.compaction_trigger_ratio,
    recent_message_limit: loaded.recent_message_limit,
    allow_cross_session_recall: loaded.allow_cross_session_recall,
    evolution_mode: loaded.evolution_mode,
    core_markdown_max_tokens: loaded.core_markdown_max_tokens,
  }
  companionCanWriteProjectDraft.value = loaded.companion_can_write_project_memory
}

async function loadRosters(gen: number) {
  const [projectRows, companionRows] = await Promise.all([
    api.listProjects({ lifecycleStatus: 'active' }).catch(() => [] as ProjectDto[]),
    companionApi.listCompanions(false).catch(() => [] as CompanionSummary[]),
  ])
  if (gen !== readGeneration) return
  projects.value = Array.isArray(projectRows) ? projectRows : []
  if (!selectedProjectId.value || !projects.value.some((p) => p.id === selectedProjectId.value)) {
    selectedProjectId.value = projects.value[0]?.id ?? ''
  }
  companions.value = Array.isArray(companionRows) ? companionRows : []
  if (!selectedCompanionId.value || !companions.value.some((c) => c.id === selectedCompanionId.value)) {
    selectedCompanionId.value = companions.value[0]?.id ?? ''
  }
}

async function loadScopeData(scope: MemoryDocumentScope, gen = readGeneration) {
  if (!canAccessBackend.value) return
  if (scope === 'project' && !selectedProjectId.value && projects.value.length > 0) {
    selectedProjectId.value = projects.value[0]!.id
  }
  if (scope === 'companion' && !selectedCompanionId.value) {
    documents.companion = null
    markdownDrafts.companion = ''
    syncCompanionStructuredFromRaw('')
    ledgerItems.companion = []
    return
  }

  const query = {
    project_id: scope === 'project' ? selectedProjectId.value || null : null,
    workspace_root: scope === 'project' ? selectedProject.value?.path ?? null : null,
    companion_id: scope === 'companion' ? selectedCompanionId.value || null : null,
  }

  const [doc, items] = await Promise.all([
    contextMemoryApi.getMemoryDocument(scope, query),
    contextMemoryApi.listMemoryItems({
      scope,
      project_id: query.project_id,
      workspace_root: query.workspace_root,
      companion_id: query.companion_id,
      include_cold: true,
      include_history: true,
    }),
  ])

  if (gen !== readGeneration) return
  documents[scope] = doc
  markdownDrafts[scope] = doc.markdown
  if (scope === 'companion') {
    syncCompanionStructuredFromRaw(doc.markdown)
    if (doc.companion_can_write_project_memory !== undefined) {
      companionCanWriteProjectDraft.value = doc.companion_can_write_project_memory
    }
  }
  ledgerItems[scope] = items
}

async function loadEvolutionProposals(gen = readGeneration) {
  if (!canAccessBackend.value || typeof api.listContextEvolutionProposals !== 'function') return
  proposalsLoading.value = true
  try {
    const statusParam = proposalFilter.value === 'all' ? undefined : proposalFilter.value
    const result = await api.listContextEvolutionProposals({
      status: statusParam,
      project_id: selectedProjectId.value || undefined,
    })
    if (gen !== readGeneration) return
    proposals.value = result.items
    pendingProposalCount.value = result.pending_count
    autoTuneLowRiskSwitch.value = result.auto_tune_low_risk_context_policy
  } catch {
    // Non-fatal when older backend does not yet expose P2 evolution endpoint.
  } finally {
    if (gen === readGeneration) proposalsLoading.value = false
  }
}

async function reload() {
  if (!canAccessBackend.value) return
  const gen = ++readGeneration
  loading.value = true
  loadError.value = null
  try {
    const [, loadedPolicy] = await Promise.all([
      loadRosters(gen),
      contextMemoryApi.getPolicy(),
    ])
    if (gen !== readGeneration) return
    applyLoadedPolicy(loadedPolicy)

    await Promise.all([
      activeTab.value !== 'policy' && activeTab.value !== 'evolution'
        ? loadScopeData(activeTab.value, gen)
        : loadScopeData('principal', gen),
      loadEvolutionProposals(gen),
    ])
  } catch (error) {
    if (gen !== readGeneration) return
    loadError.value = error instanceof Error ? error.message : t('memoryContext.errors.loadFailed')
  } finally {
    if (gen === readGeneration) loading.value = false
  }
}

onMounted(() => {
  void reload()
})

onBeforeUnmount(() => {
  readGeneration++
})

watch(canAccessBackend, (ready) => {
  if (ready) void reload()
})

watch(activeTab, (tab) => {
  if (!canAccessBackend.value) return
  if (tab === 'evolution') {
    void loadEvolutionProposals()
    return
  }
  if (tab === 'policy') return
  if (!documents[tab]) {
    void loadScopeData(tab).catch((error: unknown) => {
      loadError.value = error instanceof Error ? error.message : t('memoryContext.errors.loadFailed')
    })
  }
})

watch(proposalFilter, () => {
  if (canAccessBackend.value) {
    void loadEvolutionProposals()
  }
})

watch(selectedProjectId, () => {
  if (canAccessBackend.value && activeTab.value === 'project') {
    void loadScopeData('project').catch((error: unknown) => {
      loadError.value = error instanceof Error ? error.message : t('memoryContext.errors.loadFailed')
    })
  }
})

watch(selectedCompanionId, () => {
  if (canAccessBackend.value && activeTab.value === 'companion') {
    void loadScopeData('companion').catch((error: unknown) => {
      loadError.value = error instanceof Error ? error.message : t('memoryContext.errors.loadFailed')
    })
  }
})

function resetScopeMarkdown(scope: MemoryDocumentScope) {
  const doc = documents[scope]
  if (!doc) return
  markdownDrafts[scope] = doc.markdown
  if (scope === 'companion') {
    syncCompanionStructuredFromRaw(doc.markdown)
  }
}

async function saveScopeMarkdown(scope: MemoryDocumentScope) {
  if (savingDoc.value || !canAccessBackend.value) return
  const doc = documents[scope]
  savingDoc.value = true
  try {
    const saved = await contextMemoryApi.updateMemoryDocument(scope, {
      markdown: markdownDrafts[scope],
      project_id: scope === 'project' ? selectedProjectId.value || null : null,
      workspace_root: scope === 'project' ? selectedProject.value?.path ?? null : null,
      companion_id: scope === 'companion' ? selectedCompanionId.value || null : null,
      expected_hash: doc?.content_hash ?? null,
      etag: doc?.etag ?? null,
    })
    documents[scope] = saved
    markdownDrafts[scope] = saved.markdown
    if (scope === 'companion') {
      syncCompanionStructuredFromRaw(saved.markdown)
    }
    ledgerItems[scope] = await contextMemoryApi.listMemoryItems({
      scope,
      project_id: scope === 'project' ? selectedProjectId.value || null : null,
      workspace_root: scope === 'project' ? selectedProject.value?.path ?? null : null,
      companion_id: scope === 'companion' ? selectedCompanionId.value || null : null,
      include_cold: true,
      include_history: true,
    })
    notify.success(t('memoryContext.editor.saved'))
  } catch (error) {
    notify.error(error, { title: t('memoryContext.errors.saveFailed') })
  } finally {
    savingDoc.value = false
  }
}

async function handleAddItem(
  scope: MemoryDocumentScope,
  payload: { kind: MemoryKind; section?: string | null; content: string },
) {
  if (addingItem.value || !canAccessBackend.value) return
  addingItem.value = true
  try {
    await contextMemoryApi.createMemoryItem({
      scope,
      kind: payload.kind,
      section: payload.section ?? null,
      content: payload.content,
      project_id: scope === 'project' ? selectedProjectId.value || null : null,
      workspace_root: scope === 'project' ? selectedProject.value?.path ?? null : null,
      companion_id: scope === 'companion' ? selectedCompanionId.value || null : null,
    })
    await loadScopeData(scope)
    notify.success(t('memoryContext.ledger.addedToast'))
  } catch (error) {
    notify.error(error, { title: t('memoryContext.errors.saveFailed') })
  } finally {
    addingItem.value = false
  }
}

async function handleRevertItem(scope: MemoryDocumentScope, item: MemoryLedgerItemDto) {
  if (busyItemId.value || !canAccessBackend.value) return
  busyItemId.value = item.id
  try {
    await contextMemoryApi.revertMemoryItem(item.id)
    await loadScopeData(scope)
    notify.success(t('memoryContext.ledger.revertedToast'))
  } catch (error) {
    notify.error(error, { title: t('memoryContext.errors.saveFailed') })
  } finally {
    busyItemId.value = null
  }
}

async function handleRevokeItem(scope: MemoryDocumentScope, item: MemoryLedgerItemDto) {
  if (busyItemId.value || !canAccessBackend.value) return
  busyItemId.value = item.id
  try {
    await contextMemoryApi.deleteMemoryItem(item.id)
    await loadScopeData(scope)
    notify.success(t('memoryContext.ledger.deletedToast'))
  } catch (error) {
    notify.error(error, { title: t('memoryContext.errors.saveFailed') })
  } finally {
    busyItemId.value = null
  }
}

async function toggleCompanionWriteProjectMemory(nextValue: boolean) {
  if (savingPolicy.value || !canAccessBackend.value) return
  const previous = companionCanWriteProjectDraft.value
  companionCanWriteProjectDraft.value = nextValue
  savingPolicy.value = true
  try {
    const saved = await contextMemoryApi.updatePolicy({
      companion_can_write_project_memory: nextValue,
      expected_hash: policy.value?.content_hash ?? null,
    })
    applyLoadedPolicy(saved)
    notify.success(t('memoryContext.policy.savedToast'))
  } catch (error) {
    companionCanWriteProjectDraft.value = previous
    notify.error(error, { title: t('memoryContext.errors.saveFailed') })
  } finally {
    savingPolicy.value = false
  }
}

async function savePolicy() {
  if (savingPolicy.value || !canAccessBackend.value) return
  savingPolicy.value = true
  try {
    const adv = policyAdvancedDraft.value
    const saved = await contextMemoryApi.updatePolicy({
      strategy: policyStrategyDraft.value,
      recall_fidelity: adv.recall_fidelity ?? DEFAULT_CONTEXT_POLICY_PARAMS.recall_fidelity,
      compaction_trigger_ratio:
        adv.compaction_trigger_ratio ?? DEFAULT_CONTEXT_POLICY_PARAMS.compaction_trigger_ratio,
      recent_message_limit:
        adv.recent_message_limit ?? DEFAULT_CONTEXT_POLICY_PARAMS.recent_message_limit,
      allow_cross_session_recall:
        adv.allow_cross_session_recall ?? DEFAULT_CONTEXT_POLICY_PARAMS.allow_cross_session_recall,
      evolution_mode: adv.evolution_mode ?? DEFAULT_CONTEXT_POLICY_PARAMS.evolution_mode,
      core_markdown_max_tokens:
        adv.core_markdown_max_tokens ?? DEFAULT_CONTEXT_POLICY_PARAMS.core_markdown_max_tokens,
      companion_can_write_project_memory: companionCanWriteProjectDraft.value,
      expected_hash: policy.value?.content_hash ?? null,
    })
    applyLoadedPolicy(saved)
    notify.success(t('memoryContext.policy.savedToast'))
  } catch (error) {
    notify.error(error, { title: t('memoryContext.errors.saveFailed') })
  } finally {
    savingPolicy.value = false
  }
}

function jumpToRuntimeToml() {
  patchAgentCenterUi({ tab: 'advanced' })
  requestSettingsSection('agentCenter')
  if (typeof window !== 'undefined') {
    window.dispatchEvent(
      new CustomEvent('tinadec:open-runtime-toml', { detail: { documentId: 'runtime', section: 'context' } }),
    )
  }
  emit('open-runtime-toml')
}

function openSourceSession(sessionId: string) {
  if (!sessionId) return
  requestConversation(sessionId)
  void router?.push('/')
}

function proposalDiffKeys(proposal: ContextEvolutionProposalDto): string[] {
  const cmp = proposal.metrics_comparison
  if (!cmp) return []
  const keys = new Set<string>([
    ...Object.keys(cmp.before_values ?? {}),
    ...Object.keys(cmp.after_values ?? {}),
  ])
  return [...keys]
}

function proposalEvidenceRuns(proposal: ContextEvolutionProposalDto): string[] {
  if (Array.isArray(proposal.evidence_run_ids) && proposal.evidence_run_ids.length > 0) {
    return proposal.evidence_run_ids
  }
  return proposal.source_run_id ? [proposal.source_run_id] : []
}

function shortRunId(runId: string): string {
  return runId.length > 12 ? `${runId.slice(0, 8)}…` : runId
}

async function handleApplyProposal(proposal: ContextEvolutionProposalDto) {
  if (busyProposalId.value || !canAccessBackend.value) return
  busyProposalId.value = proposal.id
  try {
    const reason = proposalReasonDrafts[proposal.id]?.trim() || null
    const res = await api.applyContextEvolutionProposal(proposal.id, {
      reason,
      expected_content_hash: policy.value?.content_hash ?? null,
    })
    if (res.updated_toml_preview) {
      appliedTomlPreviews[proposal.id] = res.updated_toml_preview
    }
    if (res.summary) {
      appliedSummaries[proposal.id] = res.summary
    }
    const idx = proposals.value.findIndex((p) => p.id === proposal.id)
    if (idx >= 0) {
      proposals.value[idx] = res.proposal
    }
    pendingProposalCount.value = Math.max(0, pendingProposalCount.value - 1)
    if (proposal.target_scope === 'global') {
      const freshPolicy = await contextMemoryApi.getPolicy().catch(() => null)
      if (freshPolicy) applyLoadedPolicy(freshPolicy)
    }
    notify.success(t('memoryContext.evolution.appliedToast'))
  } catch (error) {
    notify.error(error, { title: t('memoryContext.errors.saveFailed') })
  } finally {
    busyProposalId.value = null
  }
}

async function handleRejectProposal(proposal: ContextEvolutionProposalDto) {
  if (busyProposalId.value || !canAccessBackend.value) return
  busyProposalId.value = proposal.id
  try {
    const reason = proposalReasonDrafts[proposal.id]?.trim() || null
    const updated = await api.rejectContextEvolutionProposal(proposal.id, reason)
    const idx = proposals.value.findIndex((p) => p.id === proposal.id)
    if (idx >= 0) {
      proposals.value[idx] = updated
    }
    pendingProposalCount.value = Math.max(0, pendingProposalCount.value - 1)
    notify.success(t('memoryContext.evolution.rejectedToast'))
  } catch (error) {
    notify.error(error, { title: t('memoryContext.errors.saveFailed') })
  } finally {
    busyProposalId.value = null
  }
}

async function handleDiagnoseProposal(proposal: ContextEvolutionProposalDto) {
  await triggerDiagnoseInComposer({ proposal })
}

async function canLeave(): Promise<boolean> {
  if (!hasAnyUnsavedChanges.value) return true
  if (typeof confirm !== 'function') return true
  return confirm({
    title: t('memoryContext.title'),
    message: t('memoryContext.unsavedLeaveMessage'),
    confirmLabel: t('memoryContext.editor.reset'),
    cancelLabel: t('common.cancel'),
    destructive: true,
  })
}

defineExpose({ canLeave, reload })
</script>

<template>
  <div class="mc-workbench" data-testid="memory-context-section">
    <!-- Header -->
    <header class="mc-header">
      <div class="mc-header-copy">
        <div class="mc-title-row">
          <Brain :size="18" class="mc-title-icon" aria-hidden="true" />
          <h2>{{ t('memoryContext.title') }}</h2>
        </div>
        <p>{{ t('memoryContext.subtitle') }}</p>
      </div>
      <UiButton
        variant="outline"
        size="sm"
        :disabled="loading || !canAccessBackend"
        data-testid="memory-context-refresh-btn"
        @click="reload"
      >
        <RefreshCw :size="14" aria-hidden="true" />
        <span>{{ t('memoryContext.refresh') }}</span>
      </UiButton>
    </header>

    <p v-if="!canAccessBackend" class="mc-host-notice" role="status" data-testid="memory-context-host-notice">
      {{ hostReason }}
    </p>
    <p v-else-if="loadError" class="mc-error-banner" role="alert" data-testid="memory-context-error">
      {{ loadError }}
    </p>

    <!-- 4-Tab Bar -->
    <div class="mc-tab-bar-row">
      <div class="mc-tabs" role="tablist" :aria-label="t('memoryContext.title')">
        <button
          v-for="tab in tabs"
          :key="tab.key"
          type="button"
          role="tab"
          :aria-selected="activeTab === tab.key"
          :class="['mc-tab-btn', { active: activeTab === tab.key }]"
          :data-testid="`memory-tab-${tab.key}`"
          @click="activeTab = tab.key"
        >
          <component :is="tab.icon" :size="15" aria-hidden="true" />
          <span>{{ tab.label }}</span>
          <span v-if="tab.badgeCount > 0" class="mc-tab-count mono" data-testid="evolution-pending-badge">
            {{ tab.badgeCount }}
          </span>
        </button>
      </div>

      <div v-if="activeTab !== 'policy' && activeTab !== 'evolution'" class="mc-view-switcher" role="group">
        <button
          type="button"
          :class="['mc-view-btn', { active: viewMode === 'split' }]"
          data-testid="memory-view-split"
          @click="viewMode = 'split'"
        >
          {{ t('memoryContext.view.split') }}
        </button>
        <button
          type="button"
          :class="['mc-view-btn', { active: viewMode === 'markdown' }]"
          data-testid="memory-view-markdown"
          @click="viewMode = 'markdown'"
        >
          {{ t('memoryContext.view.markdown') }}
        </button>
        <button
          type="button"
          :class="['mc-view-btn', { active: viewMode === 'ledger' }]"
          data-testid="memory-view-ledger"
          @click="viewMode = 'ledger'"
        >
          {{ t('memoryContext.view.ledger') }}
        </button>
      </div>
    </div>

    <!-- TAB 1: Principal (User Global Profile) -->
    <section
      v-if="activeTab === 'principal'"
      class="mc-tab-panel"
      role="tabpanel"
      data-testid="memory-panel-principal"
    >
      <div class="mc-scope-banner">
        <div class="mc-scope-banner-main">
          <UiBadge variant="secondary">{{ t('memoryContext.principal.badge') }}</UiBadge>
          <code class="mc-doc-path">{{ documents.principal?.path || 'memory/user-profile.md' }}</code>
          <span class="mc-token-pill">
            {{ t('memoryContext.budget.tokens', { used: principalBudget.used, max: principalBudget.max }) }}
          </span>
        </div>
        <p class="mc-scope-hint">{{ t('memoryContext.principal.hint') }}</p>
      </div>

      <div :class="['mc-dual-grid', `mode-${viewMode}`]">
        <div v-if="viewMode !== 'ledger'" class="mc-editor-card" data-testid="principal-markdown-card">
          <div class="mc-editor-toolbar">
            <div class="mc-editor-meta">
              <FileCode2 :size="14" aria-hidden="true" />
              <code>user-profile.md</code>
              <UiBadge variant="outline">
                {{ t('memoryContext.editor.anchors', { count: principalBudget.anchorsCount }) }}
              </UiBadge>
              <UiBadge v-if="isScopeDirty('principal')" variant="secondary">
                {{ t('memoryContext.editor.unsaved') }}
              </UiBadge>
            </div>
            <div class="mc-editor-actions">
              <UiButton
                v-if="isScopeDirty('principal')"
                variant="ghost"
                size="sm"
                :disabled="savingDoc"
                @click="resetScopeMarkdown('principal')"
              >
                <RotateCcw :size="13" aria-hidden="true" />
                <span>{{ t('memoryContext.editor.reset') }}</span>
              </UiButton>
              <UiButton
                size="sm"
                :disabled="savingDoc || !canAccessBackend || !isScopeDirty('principal')"
                data-testid="principal-save-markdown-btn"
                @click="saveScopeMarkdown('principal')"
              >
                <Save :size="13" aria-hidden="true" />
                <span>{{ t('memoryContext.editor.save') }}</span>
              </UiButton>
            </div>
          </div>

          <textarea
            v-model="markdownDrafts.principal"
            class="mc-markdown-textarea"
            spellcheck="false"
            :disabled="!canAccessBackend || savingDoc"
            :placeholder="t('memoryContext.editor.placeholder')"
            data-testid="principal-markdown-textarea"
          />
        </div>

        <MemoryScopeLedgerPanel
          v-if="viewMode !== 'markdown'"
          scope="principal"
          :items="ledgerItems.principal"
          :loading="loading"
          :busy-id="busyItemId"
          :adding="addingItem"
          :disabled="!canAccessBackend"
          @add="(payload) => handleAddItem('principal', payload)"
          @revert="(item) => handleRevertItem('principal', item)"
          @revoke="(item) => handleRevokeItem('principal', item)"
          @open-session="openSourceSession"
        />
      </div>
    </section>

    <!-- TAB 2: Project Memory -->
    <section
      v-else-if="activeTab === 'project'"
      class="mc-tab-panel"
      role="tabpanel"
      data-testid="memory-panel-project"
    >
      <div class="mc-scope-banner">
        <div class="mc-scope-selector-row">
          <div class="mc-selector-field">
            <UiLabel>{{ t('memoryContext.project.selectLabel') }}</UiLabel>
            <UiSelectField
              v-model="selectedProjectId"
              :options="projectOptions"
              :placeholder="t('memoryContext.project.noProjects')"
              :disabled="projects.length === 0 || !canAccessBackend"
              data-testid="project-memory-selector"
            />
          </div>
          <div class="mc-scope-banner-tags">
            <UiBadge variant="secondary">{{ t('memoryContext.project.badge') }}</UiBadge>
            <code class="mc-doc-path">{{ documents.project?.path || '.tinadec/memory/project-core.md' }}</code>
          </div>
        </div>
        <p class="mc-scope-hint">{{ t('memoryContext.project.hint') }}</p>
      </div>

      <!-- Core Markdown Token Budget Meter -->
      <div class="mc-budget-meter" data-testid="project-token-budget-meter">
        <div class="mc-budget-head">
          <div class="mc-budget-title">
            <Database :size="14" aria-hidden="true" />
            <strong>{{ t('memoryContext.budget.title') }}</strong>
          </div>
          <span class="mc-budget-numbers" data-testid="project-token-budget-fraction">
            {{ t('memoryContext.budget.tokens', { used: projectBudget.used, max: projectBudget.max }) }}
            ({{ projectBudget.percent }}%)
          </span>
        </div>
        <div class="mc-budget-bar" role="progressbar" :aria-valuenow="projectBudget.percent" aria-valuemin="0" aria-valuemax="100">
          <div
            :class="[
              'mc-budget-fill',
              {
                'is-warn': projectBudget.ratio >= 0.8 && projectBudget.ratio < 0.95,
                'is-danger': projectBudget.ratio >= 0.95,
              },
            ]"
            :style="{ width: `${projectBudget.percent}%` }"
          />
        </div>
        <div class="mc-budget-stats">
          <span class="mc-stat-hot" data-testid="project-hot-items-count">
            {{ t('memoryContext.budget.hotItems', { count: projectBudget.hotCount }) }}
          </span>
          <span class="mc-stat-cold" data-testid="project-cold-archive-count">
            {{ t('memoryContext.budget.coldArchive', { count: projectBudget.coldCount }) }}
          </span>
        </div>
      </div>

      <div :class="['mc-dual-grid', `mode-${viewMode}`]">
        <div v-if="viewMode !== 'ledger'" class="mc-editor-card" data-testid="project-markdown-card">
          <div class="mc-editor-toolbar">
            <div class="mc-editor-meta">
              <FileCode2 :size="14" aria-hidden="true" />
              <code>.tinadec/memory/project-core.md</code>
              <UiBadge variant="outline">
                {{ t('memoryContext.editor.anchors', { count: projectBudget.anchorsCount }) }}
              </UiBadge>
              <UiBadge v-if="isScopeDirty('project')" variant="secondary">
                {{ t('memoryContext.editor.unsaved') }}
              </UiBadge>
            </div>
            <div class="mc-editor-actions">
              <UiButton
                v-if="isScopeDirty('project')"
                variant="ghost"
                size="sm"
                :disabled="savingDoc"
                @click="resetScopeMarkdown('project')"
              >
                <RotateCcw :size="13" aria-hidden="true" />
                <span>{{ t('memoryContext.editor.reset') }}</span>
              </UiButton>
              <UiButton
                size="sm"
                :disabled="savingDoc || !canAccessBackend || !isScopeDirty('project')"
                data-testid="project-save-markdown-btn"
                @click="saveScopeMarkdown('project')"
              >
                <Save :size="13" aria-hidden="true" />
                <span>{{ t('memoryContext.editor.save') }}</span>
              </UiButton>
            </div>
          </div>

          <textarea
            v-model="markdownDrafts.project"
            class="mc-markdown-textarea"
            spellcheck="false"
            :disabled="!canAccessBackend || savingDoc"
            :placeholder="t('memoryContext.editor.placeholder')"
            data-testid="project-markdown-textarea"
          />
        </div>

        <MemoryScopeLedgerPanel
          v-if="viewMode !== 'markdown'"
          scope="project"
          :items="ledgerItems.project"
          :loading="loading"
          :busy-id="busyItemId"
          :adding="addingItem"
          :disabled="!canAccessBackend"
          @add="(payload) => handleAddItem('project', payload)"
          @revert="(item) => handleRevertItem('project', item)"
          @revoke="(item) => handleRevokeItem('project', item)"
          @open-session="openSourceSession"
        />
      </div>
    </section>

    <!-- TAB 3: Companion Private Memory -->
    <section
      v-else-if="activeTab === 'companion'"
      class="mc-tab-panel"
      role="tabpanel"
      data-testid="memory-panel-companion"
    >
      <div class="mc-scope-banner">
        <div class="mc-scope-selector-row">
          <div class="mc-selector-field">
            <UiLabel>{{ t('memoryContext.companion.selectLabel') }}</UiLabel>
            <UiSelectField
              v-model="selectedCompanionId"
              :options="companionOptions"
              :placeholder="t('memoryContext.companion.noCompanions')"
              :disabled="companions.length === 0 || !canAccessBackend"
              data-testid="companion-memory-selector"
            />
          </div>
          <div class="mc-scope-banner-tags">
            <UiBadge variant="secondary">{{ t('memoryContext.companion.badge') }}</UiBadge>
            <code class="mc-doc-path">
              {{ documents.companion?.path || `${selectedCompanionId || '<companion_id>'}/memory.md` }}
            </code>
            <span class="mc-token-pill">
              {{ t('memoryContext.budget.tokens', { used: companionBudget.used, max: companionBudget.max }) }}
            </span>
          </div>
        </div>
        <p class="mc-scope-hint">{{ t('memoryContext.companion.hint') }}</p>

        <div class="mc-companion-permission-row">
          <div class="mc-permission-copy">
            <strong>{{ t('memoryContext.companion.writeProjectLabel') }}</strong>
            <small>{{ t('memoryContext.companion.writeProjectHint') }}</small>
          </div>
          <UiSwitch
            :model-value="companionCanWriteProjectDraft"
            :disabled="!canAccessBackend || savingPolicy"
            data-testid="companion-write-project-memory-switch"
            @update:model-value="toggleCompanionWriteProjectMemory"
          />
        </div>
      </div>

      <div :class="['mc-dual-grid', `mode-${viewMode}`]">
        <div v-if="viewMode !== 'ledger'" class="mc-editor-card" data-testid="companion-markdown-card">
          <div class="mc-editor-toolbar">
            <div class="mc-companion-mode-pills" role="group">
              <button
                type="button"
                :class="['mc-view-btn', { active: companionEditorMode === 'structured' }]"
                data-testid="companion-mode-structured"
                @click="companionEditorMode = 'structured'"
              >
                {{ t('memoryContext.companion.structuredMode') }}
              </button>
              <button
                type="button"
                :class="['mc-view-btn', { active: companionEditorMode === 'raw' }]"
                data-testid="companion-mode-raw"
                @click="companionEditorMode = 'raw'"
              >
                {{ t('memoryContext.companion.rawMarkdownMode') }}
              </button>
            </div>
            <div class="mc-editor-actions">
              <UiBadge v-if="isScopeDirty('companion')" variant="secondary">
                {{ t('memoryContext.editor.unsaved') }}
              </UiBadge>
              <UiButton
                v-if="isScopeDirty('companion')"
                variant="ghost"
                size="sm"
                :disabled="savingDoc"
                @click="resetScopeMarkdown('companion')"
              >
                <RotateCcw :size="13" aria-hidden="true" />
                <span>{{ t('memoryContext.editor.reset') }}</span>
              </UiButton>
              <UiButton
                size="sm"
                :disabled="savingDoc || !canAccessBackend || !selectedCompanionId || !isScopeDirty('companion')"
                data-testid="companion-save-markdown-btn"
                @click="saveScopeMarkdown('companion')"
              >
                <Save :size="13" aria-hidden="true" />
                <span>{{ t('memoryContext.editor.save') }}</span>
              </UiButton>
            </div>
          </div>

          <div
            v-if="companionEditorMode === 'structured'"
            class="mc-companion-sections-grid"
            data-testid="companion-structured-sections"
          >
            <div
              v-for="spec in COMPANION_MEMORY_SECTIONS"
              :key="spec.key"
              class="mc-companion-section-box"
              :data-testid="`companion-section-${spec.key}`"
            >
              <UiLabel>{{ t(`memoryContext.companion.sections.${spec.key}`) }}</UiLabel>
              <UiTextarea
                :model-value="companionSections[spec.key]"
                :rows="4"
                :disabled="!canAccessBackend || !selectedCompanionId || savingDoc"
                :placeholder="t('memoryContext.companion.sectionPlaceholder')"
                :data-testid="`companion-section-input-${spec.key}`"
                @update:model-value="(val: string) => onCompanionSectionInput(spec.key, val)"
              />
            </div>
          </div>

          <textarea
            v-else
            :value="markdownDrafts.companion"
            class="mc-markdown-textarea"
            spellcheck="false"
            :disabled="!canAccessBackend || !selectedCompanionId || savingDoc"
            :placeholder="t('memoryContext.editor.placeholder')"
            data-testid="companion-raw-textarea"
            @input="onCompanionRawInput(($event.target as HTMLTextAreaElement).value)"
          />
        </div>

        <MemoryScopeLedgerPanel
          v-if="viewMode !== 'markdown'"
          scope="companion"
          :items="ledgerItems.companion"
          :loading="loading"
          :busy-id="busyItemId"
          :adding="addingItem"
          :disabled="!canAccessBackend || !selectedCompanionId"
          @add="(payload) => handleAddItem('companion', payload)"
          @revert="(item) => handleRevertItem('companion', item)"
          @revoke="(item) => handleRevokeItem('companion', item)"
          @open-session="openSourceSession"
        />
      </div>
    </section>

    <!-- TAB 4: Policy & TOML -->
    <section
      v-else-if="activeTab === 'policy'"
      class="mc-tab-panel"
      role="tabpanel"
      data-testid="memory-panel-policy"
    >
      <div
        v-if="pendingProposalCount > 0"
        class="mc-evolution-callout"
        data-testid="policy-evolution-callout"
      >
        <div class="mc-evolution-callout__copy">
          <Sparkles :size="14" aria-hidden="true" />
          <span>{{ t('memoryContext.evolution.pendingCallout', { count: pendingProposalCount }) }}</span>
        </div>
        <UiButton
          variant="outline"
          size="sm"
          data-testid="policy-jump-evolution-btn"
          @click="activeTab = 'evolution'"
        >
          <span>{{ t('memoryContext.evolution.reviewAction') }}</span>
        </UiButton>
      </div>

      <!-- Always-On Informative Status Pills (No redundant off-switches) -->
      <div class="mc-always-on-card" data-testid="always-on-primitives">
        <h4>{{ t('memoryContext.policy.alwaysOnTitle') }}</h4>
        <div class="mc-always-on-grid">
          <div class="mc-always-pill-item" data-testid="always-on-kv-cache-pill">
            <div class="mc-always-pill-top">
              <CheckCircle2 :size="15" class="mc-always-icon" aria-hidden="true" />
              <strong>{{ t('memoryContext.policy.kvCachePill') }}</strong>
            </div>
            <p>{{ t('memoryContext.policy.kvCacheDesc') }}</p>
          </div>
          <div class="mc-always-pill-item" data-testid="always-on-micro-compact-pill">
            <div class="mc-always-pill-top">
              <CheckCircle2 :size="15" class="mc-always-icon" aria-hidden="true" />
              <strong>{{ t('memoryContext.policy.microCompactPill') }}</strong>
            </div>
            <p>{{ t('memoryContext.policy.microCompactDesc') }}</p>
          </div>
        </div>
      </div>

      <!-- 3-Tier Context Strategy + Collapsible Advanced Parameters -->
      <div class="mc-policy-editor-card">
        <ModeContextStrategySection
          :strategy="policyStrategyDraft"
          :advanced="policyAdvancedDraft"
          :readonly="!canAccessBackend || savingPolicy"
          :allow-inherit="false"
          @update:strategy="(val) => { if (val) policyStrategyDraft = val }"
          @update:advanced="(val) => { policyAdvancedDraft = val }"
        />

        <div class="mc-policy-save-bar">
          <UiBadge v-if="isPolicyDirty" variant="secondary">
            {{ t('memoryContext.editor.unsaved') }}
          </UiBadge>
          <UiButton
            :disabled="!canAccessBackend || savingPolicy || !isPolicyDirty"
            data-testid="save-context-policy-btn"
            @click="savePolicy"
          >
            <Save :size="14" aria-hidden="true" />
            <span>{{ t('memoryContext.policy.saveBtn') }}</span>
          </UiButton>
        </div>
      </div>

      <!-- Richly Commented runtime.toml Jump Card -->
      <div class="mc-toml-jump-card" data-testid="runtime-toml-jump-card">
        <div class="mc-toml-jump-head">
          <div>
            <h4>{{ t('memoryContext.policy.tomlTitle') }}</h4>
            <p>{{ t('memoryContext.policy.tomlDesc') }}</p>
          </div>
          <UiButton
            variant="outline"
            size="sm"
            data-testid="jump-runtime-toml-btn"
            @click="jumpToRuntimeToml"
          >
            <span>{{ t('memoryContext.policy.jumpTomlBtn') }}</span>
            <ArrowUpRight :size="14" aria-hidden="true" />
          </UiButton>
        </div>
        <pre class="mc-toml-preview"><code>{{ tomlPreviewText }}</code></pre>
      </div>
    </section>

    <!-- TAB 5: P2 Context Self-Evolution Proposals Review Queue -->
    <section
      v-else
      class="mc-tab-panel"
      role="tabpanel"
      data-testid="memory-panel-evolution"
    >
      <div class="mc-scope-banner" data-testid="evolution-posture-banner">
        <div class="mc-scope-banner-main">
          <UiBadge variant="secondary">{{ t('memoryContext.evolution.conservativeBadge') }}</UiBadge>
          <span class="mc-token-pill">
            {{
              autoTuneLowRiskSwitch
                ? t('memoryContext.evolution.autoTuneEnabled')
                : t('memoryContext.evolution.autoTuneDisabled')
            }}
          </span>
        </div>
        <p class="mc-scope-hint">{{ t('memoryContext.evolution.postureHint') }}</p>
      </div>

      <div class="mc-evolution-toolbar">
        <div class="mc-view-switcher" role="group" :aria-label="t('memoryContext.evolution.filterLabel')">
          <button
            v-for="filterKey in (['pending', 'applied', 'rejected', 'all'] as const)"
            :key="filterKey"
            type="button"
            :class="['mc-view-btn', { active: proposalFilter === filterKey }]"
            :data-testid="`evolution-filter-${filterKey}`"
            @click="proposalFilter = filterKey"
          >
            {{ t(`memoryContext.evolution.filters.${filterKey}`) }}
          </button>
        </div>

        <UiButton
          variant="outline"
          size="sm"
          :disabled="proposalsLoading || !canAccessBackend"
          data-testid="evolution-refresh-btn"
          @click="loadEvolutionProposals()"
        >
          <RefreshCw :size="13" aria-hidden="true" />
          <span>{{ t('memoryContext.refresh') }}</span>
        </UiButton>
      </div>

      <div v-if="proposalsLoading && proposals.length === 0" class="mc-evolution-empty">
        {{ t('memoryContext.evolution.loading') }}
      </div>
      <div v-else-if="proposals.length === 0" class="mc-evolution-empty" data-testid="evolution-proposals-empty">
        {{ t('memoryContext.evolution.empty') }}
      </div>

      <div v-else class="mc-proposal-list" data-testid="evolution-proposal-list">
        <article
          v-for="proposal in proposals"
          :key="proposal.id"
          class="mc-proposal-card"
          :data-testid="`evolution-proposal-${proposal.id}`"
        >
          <header class="mc-proposal-head">
            <div class="mc-proposal-badges">
              <UiBadge variant="secondary">
                {{
                  proposal.kind === 'prompt_dedup_proposal'
                    ? t('memoryContext.evolution.kinds.prompt_dedup_proposal')
                    : t('memoryContext.evolution.kinds.context_policy_proposal')
                }}
              </UiBadge>
              <UiBadge variant="outline">
                {{
                  proposal.target_scope === 'mode' && (proposal.target_mode_name || proposal.target_mode_slug)
                    ? `${t('memoryContext.evolution.scopes.mode')}: ${proposal.target_mode_name || proposal.target_mode_slug}`
                    : proposal.target_scope === 'workspace'
                      ? t('memoryContext.evolution.scopes.workspace')
                      : t('memoryContext.evolution.scopes.global')
                }}
              </UiBadge>
              <UiBadge :variant="proposal.status === 'applied' ? 'default' : 'outline'">
                {{
                  proposal.status === 'applied'
                    ? t('memoryContext.evolution.status.applied')
                    : proposal.status === 'rejected'
                      ? t('memoryContext.evolution.status.rejected')
                      : t('memoryContext.evolution.status.pending')
                }}
              </UiBadge>
            </div>
            <span class="mc-token-pill">
              {{ Math.round((proposal.confidence ?? 0) * 100) }}%
            </span>
          </header>

          <div class="mc-proposal-copy">
            <h4>{{ proposal.title || proposal.summary }}</h4>
            <p v-if="proposal.title && proposal.summary !== proposal.title">{{ proposal.summary }}</p>
            <p v-if="proposal.rationale" class="mc-proposal-rationale">{{ proposal.rationale }}</p>
          </div>

          <div
            v-if="proposalEvidenceRuns(proposal).length > 0"
            class="mc-proposal-runs"
            data-testid="proposal-evidence-runs"
          >
            <span class="mc-proposal-runs__label">{{ t('memoryContext.evolution.evidenceRuns') }}</span>
            <code
              v-for="evRunId in proposalEvidenceRuns(proposal)"
              :key="evRunId"
              class="mc-doc-path"
              :title="evRunId"
              data-testid="proposal-evidence-run"
            >{{ shortRunId(evRunId) }}</code>
          </div>

          <!-- Policy Before -> After Metrics Comparison -->
          <div
            v-if="proposal.metrics_comparison"
            class="mc-proposal-evidence-box"
            data-testid="proposal-before-after"
          >
            <div class="mc-proposal-metrics-strip mono">
              <span>
                {{
                  t('memoryContext.evolution.degradedWindow', {
                    degraded: proposal.metrics_comparison.degraded_run_count,
                    total: proposal.metrics_comparison.window_run_count,
                  })
                }}
              </span>
              <span>
                {{
                  t('memoryContext.evolution.peakOccupancy', {
                    pct: Math.round(proposal.metrics_comparison.avg_peak_occupancy_pct),
                  })
                }}
              </span>
              <span>
                {{
                  t('memoryContext.evolution.highLowErrorRate', {
                    high: Math.round(proposal.metrics_comparison.high_occupancy_error_rate * 100),
                    low: Math.round(proposal.metrics_comparison.low_occupancy_error_rate * 100),
                  })
                }}
              </span>
            </div>

            <div v-if="proposalDiffKeys(proposal).length > 0" class="mc-diff-grid mono">
              <div
                v-for="diffKey in proposalDiffKeys(proposal)"
                :key="diffKey"
                class="mc-diff-row"
              >
                <span class="mc-diff-key">{{ diffKey }}</span>
                <span class="mc-diff-before">{{ proposal.metrics_comparison.before_values?.[diffKey] ?? '—' }}</span>
                <span class="mc-diff-arrow">&rarr;</span>
                <strong class="mc-diff-after">{{ proposal.metrics_comparison.after_values?.[diffKey] ?? '—' }}</strong>
              </div>
            </div>

            <p v-if="proposal.metrics_comparison.expected_benefit" class="mc-proposal-benefit">
              {{ proposal.metrics_comparison.expected_benefit }}
            </p>
          </div>

          <!-- Prompt / Skill Deduplication Winnowing Evidence -->
          <div
            v-if="proposal.dedup_evidence"
            class="mc-proposal-evidence-box"
            data-testid="proposal-dedup-evidence"
          >
            <div class="mc-proposal-metrics-strip mono">
              <strong>{{ proposal.dedup_evidence.source_a_label }} &harr; {{ proposal.dedup_evidence.source_b_label }}</strong>
              <span>{{ proposal.dedup_evidence.line_range_label }}</span>
              <span>
                {{ t('memoryContext.evolution.wastedTokens', { tokens: proposal.dedup_evidence.estimated_wasted_tokens }) }}
              </span>
            </div>
            <p v-if="proposal.dedup_evidence.suggested_remediation" class="mc-proposal-benefit">
              {{ proposal.dedup_evidence.suggested_remediation }}
            </p>
            <pre v-if="proposal.dedup_evidence.excerpt" class="mc-toml-preview">{{ proposal.dedup_evidence.excerpt }}</pre>
          </div>

          <!-- Applied TOML / Mode Version Audit Receipt -->
          <div
            v-if="appliedTomlPreviews[proposal.id] || appliedSummaries[proposal.id] || proposal.applied_toml_path || proposal.applied_mode_version"
            class="mc-proposal-receipt"
            data-testid="proposal-applied-receipt"
          >
            <p v-if="appliedSummaries[proposal.id] || proposal.decision_reason">
              {{ appliedSummaries[proposal.id] || proposal.decision_reason }}
            </p>
            <code v-if="proposal.applied_toml_path" class="mc-doc-path">{{ proposal.applied_toml_path }}</code>
            <pre v-if="appliedTomlPreviews[proposal.id]" class="mc-toml-preview"><code>{{ appliedTomlPreviews[proposal.id] }}</code></pre>
          </div>

          <footer class="mc-proposal-actions">
            <UiInput
              v-if="proposal.status === 'pending'"
              v-model="proposalReasonDrafts[proposal.id]"
              class="mc-proposal-reason-input"
              :disabled="busyProposalId === proposal.id || !canAccessBackend"
              :placeholder="t('memoryContext.evolution.reasonPlaceholder')"
              data-testid="proposal-reason-input"
            />
            <div class="mc-proposal-buttons">
              <UiButton
                variant="ghost"
                size="sm"
                data-testid="proposal-diagnose-btn"
                @click="handleDiagnoseProposal(proposal)"
              >
                <Sparkles :size="13" aria-hidden="true" />
                <span>{{ t('trajectoryEval.actions.diagnoseInComposer') }}</span>
              </UiButton>
              <UiButton
                v-if="proposal.status === 'pending'"
                variant="outline"
                size="sm"
                :disabled="busyProposalId === proposal.id || !canAccessBackend"
                data-testid="proposal-reject-btn"
                @click="handleRejectProposal(proposal)"
              >
                <ThumbsDown :size="13" aria-hidden="true" />
                <span>{{ t('memoryContext.evolution.rejectBtn') }}</span>
              </UiButton>
              <UiButton
                v-if="proposal.status === 'pending'"
                size="sm"
                :disabled="busyProposalId === proposal.id || !canAccessBackend"
                data-testid="proposal-apply-btn"
                @click="handleApplyProposal(proposal)"
              >
                <CheckCircle2 :size="13" aria-hidden="true" />
                <span>{{ t('memoryContext.evolution.applyBtn') }}</span>
              </UiButton>
            </div>
          </footer>
        </article>
      </div>
    </section>
  </div>
</template>

<style scoped>
.mc-workbench {
  display: flex;
  flex-direction: column;
  gap: 14px;
  min-width: 0;
}

.mc-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.mc-header-copy {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.mc-title-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.mc-title-icon {
  color: var(--accent-primary);
}

.mc-title-row h2 {
  margin: 0;
  font-size: 18px;
  font-weight: 700;
  color: var(--text-primary);
}

.mc-header-copy p {
  margin: 0;
  font-size: 13px;
  line-height: 1.5;
  color: var(--text-secondary);
}

.mc-host-notice,
.mc-error-banner {
  margin: 0;
  padding: 10px 12px;
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  font-size: 12px;
  color: var(--text-secondary);
}

.mc-error-banner {
  border-color: var(--accent-danger);
  color: var(--accent-danger);
}

.mc-tab-bar-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}

.mc-tabs {
  display: inline-flex;
  flex-wrap: wrap;
  gap: 4px;
  padding: 4px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

.mc-tab-btn {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  gap: 6px;
  min-height: var(--control-height-sm, 30px);
  padding: 5px 12px;
  border: 0;
  border-radius: var(--radius-control, 6px);
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
}

.mc-tab-btn:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.mc-tab-btn:focus-visible,
.mc-view-btn:focus-visible {
  outline: var(--focus-ring-width, 2px) solid var(--focus-ring-color, var(--accent-primary));
  outline-offset: 2px;
}

.mc-tab-btn.active {
  background: var(--surface-selected);
  color: var(--accent-primary);
  font-weight: 600;
}

.mc-view-switcher,
.mc-companion-mode-pills {
  display: inline-flex;
  gap: 3px;
  padding: 3px;
  border-radius: var(--radius-control-md, 8px);
  border: 1px solid var(--border-muted);
  background: var(--surface-input);
}

.mc-view-btn {
  padding: 4px 9px;
  border: 0;
  border-radius: var(--radius-control, 6px);
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 500;
  white-space: nowrap;
  flex-shrink: 0;
  cursor: pointer;
}

.mc-view-btn.active {
  background: var(--surface-selected);
  color: var(--accent-primary);
  font-weight: 600;
}

.mc-tab-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
}

.mc-scope-banner {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px 14px;
  border-radius: 11px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

.mc-scope-banner-main,
.mc-scope-banner-tags {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.mc-scope-selector-row {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.mc-selector-field {
  display: flex;
  flex-direction: column;
  gap: 5px;
  min-width: 240px;
  flex: 1 1 260px;
  max-width: 420px;
}

.mc-doc-path,
.mc-token-pill {
  font-family: var(--font-mono);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
  padding: 2px 8px;
  border-radius: var(--radius-control, 6px);
  background: var(--surface-input);
  color: var(--text-secondary);
  white-space: nowrap;
}

.mc-scope-hint {
  margin: 0;
  font-size: 12px;
  line-height: 1.5;
  color: var(--text-secondary);
}

.mc-companion-permission-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding-top: 8px;
  border-top: 1px solid var(--border-muted);
}

.mc-permission-copy {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.mc-permission-copy strong {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
}

.mc-permission-copy small {
  font-size: 11px;
  color: var(--text-secondary);
}

.mc-budget-meter {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px 14px;
  border-radius: 11px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

.mc-budget-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.mc-budget-title {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  color: var(--text-primary);
}

.mc-budget-numbers {
  font-family: var(--font-mono);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
  color: var(--text-secondary);
}

.mc-budget-bar {
  width: 100%;
  height: 8px;
  border-radius: 999px;
  background: var(--surface-input);
  overflow: hidden;
}

.mc-budget-fill {
  height: 100%;
  border-radius: 999px;
  background: var(--accent-primary);
  transition: width 0.2s ease;
}

.mc-budget-fill.is-warn {
  background: var(--accent-warning);
}

.mc-budget-fill.is-danger {
  background: var(--accent-danger);
}

.mc-budget-stats {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
  font-size: 11px;
  color: var(--text-secondary);
}

.mc-stat-hot {
  font-weight: 600;
  color: var(--text-primary);
}

.mc-dual-grid {
  display: grid;
  gap: 12px;
  align-items: start;
}

.mc-dual-grid.mode-split {
  grid-template-columns: repeat(auto-fit, minmax(340px, 1fr));
}

.mc-dual-grid.mode-markdown,
.mc-dual-grid.mode-ledger {
  grid-template-columns: 1fr;
}

.mc-editor-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px;
  border-radius: 12px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  min-width: 0;
}

.mc-editor-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.mc-editor-meta,
.mc-editor-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.mc-editor-meta code {
  font-family: var(--font-mono);
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
}

.mc-markdown-textarea {
  width: 100%;
  min-height: 360px;
  padding: 10px 12px;
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: var(--surface-input);
  color: var(--text-primary);
  font-family: var(--font-mono);
  font-size: 12px;
  line-height: 1.6;
  resize: vertical;
}

.mc-markdown-textarea:focus {
  border-color: var(--border-input-focus);
  outline: none;
}

.mc-companion-sections-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
  gap: 10px;
}

.mc-companion-section-box {
  display: flex;
  flex-direction: column;
  gap: 5px;
  padding: 10px;
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.mc-always-on-card,
.mc-policy-editor-card,
.mc-toml-jump-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px;
  border-radius: 12px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

.mc-always-on-card h4,
.mc-toml-jump-card h4 {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.mc-always-on-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
  gap: 10px;
}

.mc-always-pill-item {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 10px 12px;
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.mc-always-pill-top {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-primary);
}

.mc-always-icon {
  color: var(--accent-success);
  flex-shrink: 0;
}

.mc-always-pill-item p {
  margin: 0;
  font-size: 11px;
  line-height: 1.5;
  color: var(--text-secondary);
}

.mc-policy-save-bar {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  padding-top: 6px;
  border-top: 1px solid var(--border-muted);
}

.mc-toml-jump-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.mc-toml-jump-head p {
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--text-secondary);
}

.mc-toml-preview {
  margin: 0;
  padding: 10px 12px;
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: var(--surface-input);
  color: var(--text-primary);
  font-family: var(--font-mono);
  font-size: 11px;
  line-height: 1.55;
  overflow-x: auto;
}

.mc-tab-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 18px;
  height: 18px;
  padding: 0 5px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-input);
  color: var(--text-primary);
  font-family: var(--font-mono);
  font-size: 10.5px;
  font-weight: 600;
}

.mc-evolution-callout {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
  padding: 10px 14px;
  border-radius: 11px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

.mc-evolution-callout__copy {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  color: var(--text-primary);
}

.mc-evolution-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}

.mc-evolution-empty {
  padding: 24px 16px;
  border-radius: 11px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  text-align: center;
  font-size: 12px;
  color: var(--text-muted);
}

.mc-proposal-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.mc-proposal-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px;
  border-radius: 12px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  min-width: 0;
}

.mc-proposal-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.mc-proposal-badges {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.mc-proposal-copy {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.mc-proposal-copy h4 {
  margin: 0;
  font-size: 13.5px;
  font-weight: 600;
  color: var(--text-primary);
}

.mc-proposal-copy p {
  margin: 0;
  font-size: 12px;
  line-height: 1.5;
  color: var(--text-secondary);
}

.mc-proposal-rationale {
  color: var(--text-muted) !important;
}

.mc-proposal-runs {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.mc-proposal-runs__label {
  font-size: 11px;
  color: var(--text-muted);
}

.mc-proposal-evidence-box,
.mc-proposal-receipt {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.mc-proposal-receipt p,
.mc-proposal-benefit {
  margin: 0;
  font-size: 11.5px;
  line-height: 1.45;
  color: var(--text-secondary);
}

.mc-proposal-metrics-strip {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  font-family: var(--font-mono);
  font-size: 11px;
  color: var(--text-secondary);
}

.mc-diff-grid {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-family: var(--font-mono);
  font-size: 11px;
}

.mc-diff-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  padding: 4px 8px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-input);
}

.mc-diff-key {
  color: var(--text-secondary);
}

.mc-diff-before {
  color: var(--text-muted);
}

.mc-diff-arrow {
  color: var(--text-muted);
}

.mc-diff-after {
  color: var(--text-primary);
}

.mc-proposal-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
  padding-top: 6px;
  border-top: 1px solid var(--border-muted);
}

.mc-proposal-reason-input {
  flex: 1 1 220px;
  max-width: 360px;
}

.mc-proposal-buttons {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  margin-left: auto;
}

@media (prefers-reduced-motion: reduce) {
  .mc-budget-fill {
    transition: none;
  }
}
</style>
