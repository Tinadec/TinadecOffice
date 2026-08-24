<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Activity,
  Eye,
  FileText,
  GitBranch,
  History,
  Search,
  ThumbsDown,
  ThumbsUp,
  Undo2,
  Workflow,
} from '@lucide/vue'
import { UiBadge, UiButton, UiCard, UiInput, UiLabel } from '@/components/ui'
import PromptPipelineCanvas from '@/components/canvas/PromptPipelineCanvas.vue'
import {
  api,
  type PromptContextPreviewDto,
  type PromptFragmentDto,
  type PromptFragmentEffectivenessDto,
  type PromptFragmentVersionDto,
  type PromptPipelineDto,
} from '@/api'
import { useNotifications } from '@/composables/useNotifications'

const { t } = useI18n()
const { notify } = useNotifications()

// ── shared view state ──
const promptCanvasMode = ref<'fragments' | 'canvas'>('fragments')
const fragLoading = ref(false)

// ── pipelines (canvas view) ──
const pipelines = ref<PromptPipelineDto[]>([])
const pipelineCanvasSelectedId = ref<string | null>(null)
const pipelineCanvasPipeline = computed(() =>
  pipelineCanvasSelectedId.value ? pipelines.value.find((p) => p.id === pipelineCanvasSelectedId.value) ?? null : null
)

async function loadPipelines() {
  try {
    const list = await api.listPromptPipelines()
    pipelines.value = Array.isArray(list) ? list : []
    if (!pipelineCanvasSelectedId.value && pipelines.value[0]) pipelineCanvasSelectedId.value = pipelines.value[0].id
  } catch {
    pipelines.value = []
  }
}

function handlePipelineCanvasUpdateNodes(payload: unknown) {
  if (!pipelineCanvasSelectedId.value) return
  const target = pipelines.value.find((p) => p.id === pipelineCanvasSelectedId.value)
  if (target) target.nodes = payload as PromptPipelineDto['nodes']
}
function handlePipelineCanvasUpdateEdges(payload: unknown) {
  if (!pipelineCanvasSelectedId.value) return
  const target = pipelines.value.find((p) => p.id === pipelineCanvasSelectedId.value)
  if (target) target.edges = payload as PromptPipelineDto['edges']
}

async function publishPipeline(pipelineId: string) {
  try {
    await api.publishPromptPipeline(pipelineId)
    notify.success({ message: t('settings.pipelinePublished') })
    await loadPipelines()
  } catch (e) {
    notify.error(e, { title: t('settings.promptEngineering') })
  }
}

// ── fragments (list/detail view) ──
const fragments = ref<PromptFragmentDto[]>([])
const fragQuery = ref('')
const fragEnabledFilter = ref<'all' | 'enabled' | 'disabled'>('all')
const selectedFragmentId = ref('')
const fragmentVersions = ref<PromptFragmentVersionDto[]>([])
const fragmentEffectiveness = ref<PromptFragmentEffectivenessDto | null>(null)
const promptPreview = ref<PromptContextPreviewDto | null>(null)
const promptPreviewAgentId = ref('meeting')
const promptPreviewMode = ref('')
const promptPreviewSessionId = ref('')
const previewBusy = ref(false)

const filteredFragments = computed(() => {
  const q = fragQuery.value.trim().toLowerCase()
  return fragments.value.filter((f) => {
    if (fragEnabledFilter.value === 'enabled' && !f.enabled) return false
    if (fragEnabledFilter.value === 'disabled' && f.enabled) return false
    if (!q) return true
    return `${f.title} ${f.key} ${f.category}`.toLowerCase().includes(q)
  })
})
const selectedFragment = computed(() => fragments.value.find((f) => f.id === selectedFragmentId.value) ?? null)
const sortedVersions = computed(() => [...fragmentVersions.value].sort((a, b) => b.version - a.version))

async function loadFragments() {
  fragLoading.value = true
  try {
    const [fragmentList, effList] = await Promise.all([
      api.listPromptFragments(),
      Promise.resolve(null as PromptFragmentEffectivenessDto[] | null),
    ])
    fragments.value = Array.isArray(fragmentList) ? fragmentList : []
    void effList
    if (!selectedFragmentId.value && fragments.value.length > 0) await selectFragment(fragments.value[0])
  } catch (err) {
    notify.error(err, { title: t('settings.promptLoadFailed') })
  } finally {
    fragLoading.value = false
  }
}

async function selectFragment(fragment: PromptFragmentDto) {
  const fid = fragment.id
  selectedFragmentId.value = fid
  fragmentVersions.value = []
  fragmentEffectiveness.value = null
  try {
    const versionList = await api.listPromptFragmentVersions(fid)
    if (selectedFragmentId.value !== fid) return
    fragmentVersions.value = Array.isArray(versionList) ? versionList : []
    const eff = await api.getPromptFragmentEffectiveness(fid).catch(() => null)
    if (selectedFragmentId.value !== fid) return
    fragmentEffectiveness.value = eff as PromptFragmentEffectivenessDto | null
  } catch (e) {
    notify.error(e, { title: t('settings.fragmentDetailFailed') })
  }
}

async function rollbackVersion(version: number) {
  if (!selectedFragment.value) return
  try {
    await api.rollbackPromptFragment(selectedFragment.value.id, version)
    notify.success(t('settings.rollbackDone', { version }))
    await selectFragment(selectedFragment.value)
  } catch (e) {
    notify.error(e, { title: t('settings.rollbackFailed') })
  }
}

async function generatePromptPreview() {
  previewBusy.value = true
  try {
    promptPreview.value = await api.previewPromptContext({
      agent_id: promptPreviewAgentId.value.trim(),
      mode: promptPreviewMode.value || undefined,
      session_id: promptPreviewSessionId.value.trim() || undefined,
    })
  } catch (e) {
    notify.error(e, { title: t('settings.previewFailed') })
  } finally {
    previewBusy.value = false
  }
}

async function refreshAll() {
  await Promise.all([loadPipelines(), loadFragments()])
}

onMounted(() => { void refreshAll() })

defineExpose({ refreshAll })
</script>

<template>
  <section class="center-resource-section agent-prompts-panel">
    <div class="center-resource-heading">
      <div>
        <h3>{{ t('settings.promptEngineering') }}</h3>
        <p>{{ t('settings.promptsPanelHint') }}</p>
      </div>
      <div class="ac-prompt-counts">
        <UiBadge variant="outline">{{ fragments.length }} {{ t('settings.fragmentCountLabel') }}</UiBadge>
        <UiBadge variant="outline">{{ pipelines.length }} {{ t('settings.pipelineCountLabel') }}</UiBadge>
      </div>
    </div>

    <div class="ac-prompt-toolbar">
      <div class="model-provider-filters" role="group" aria-label="prompt-view">
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

    <!-- Canvas 视图 -->
    <template v-if="promptCanvasMode === 'canvas'">
      <div class="ac-pipeline-picker">
        <UiLabel>{{ t('settings.pipelinePicker') }}</UiLabel>
        <select v-model="pipelineCanvasSelectedId" class="settings-select ac-pipeline-select">
          <option v-for="p in pipelines" :key="p.id" :value="p.id">{{ p.name ?? p.title ?? p.id.slice(0, 8) }} · {{ p.status ?? 'draft' }}</option>
        </select>
      </div>
      <PromptPipelineCanvas
        v-if="pipelineCanvasPipeline"
        :pipeline="pipelineCanvasPipeline"
        @update:nodes="handlePipelineCanvasUpdateNodes"
        @update:edges="handlePipelineCanvasUpdateEdges"
      />
      <div class="ac-pipeline-grid">
        <UiCard v-for="p in pipelines" :key="p.id">
          <template #content>
            <div class="ac-pipeline-card-head">
              {{ p.name ?? p.title ?? p.id.slice(0, 8) }}
              <UiBadge variant="outline">{{ p.status ?? 'draft' }}</UiBadge>
            </div>
            <div class="quiet ac-pipeline-card-desc">{{ p.description ?? '—' }}</div>
            <div class="ac-pipeline-card-actions">
              <UiButton size="xs" @click="publishPipeline(p.id)">{{ t('settings.publishAction') }}</UiButton>
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
        <div class="model-provider-filters" role="group" aria-label="enabled">
          <button :class="{ active: fragEnabledFilter === 'all' }" :aria-pressed="fragEnabledFilter === 'all'" @click="fragEnabledFilter = 'all'">{{ t('settings.allStatuses') }}</button>
          <button :class="{ active: fragEnabledFilter === 'enabled' }" :aria-pressed="fragEnabledFilter === 'enabled'" @click="fragEnabledFilter = 'enabled'">{{ t('settings.enabledStatus') }}</button>
          <button :class="{ active: fragEnabledFilter === 'disabled' }" :aria-pressed="fragEnabledFilter === 'disabled'" @click="fragEnabledFilter = 'disabled'">{{ t('settings.disabledStatus') }}</button>
        </div>
        <span class="model-provider-count">{{ filteredFragments.length }} / {{ fragments.length }}</span>
      </div>

      <div class="center-workbench workbench-duo">
        <aside class="center-resource-rail pe-fragment-list">
          <div class="pe-list-header">
            <h3>Fragments</h3>
            <UiBadge variant="outline">{{ filteredFragments.length }}</UiBadge>
          </div>
          <div v-if="filteredFragments.length === 0" class="quiet pe-empty">{{ t('agentCenter.empty.noPrompts') }}</div>
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
        </aside>

        <main class="center-resource-stage pe-detail">
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
                  <div class="pe-metric"><Activity :size="14" /><div><span>Score</span><strong>{{ (fragmentEffectiveness.effectiveness_score * 100).toFixed(0) }}%</strong></div></div>
                  <div class="pe-metric"><History :size="14" /><div><span>v</span><strong>{{ fragmentEffectiveness.active_version }}</strong></div></div>
                  <div class="pe-metric"><ThumbsUp :size="14" /><div><span>+</span><strong>{{ fragmentEffectiveness.positive_signals }}</strong></div></div>
                  <div class="pe-metric"><ThumbsDown :size="14" /><div><span>-</span><strong>{{ fragmentEffectiveness.negative_signals }}</strong></div></div>
                </div>

                <div class="pe-section">
                  <div class="pe-section-head">
                    <div class="pe-section-title"><GitBranch :size="14" /><span>{{ t('settings.currentContent') }}</span></div>
                  </div>
                  <textarea :value="selectedFragment.content" class="pe-content-textarea" rows="6" readonly></textarea>
                </div>
              </template>
            </UiCard>

            <UiCard class="pe-detail-card">
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
                        <UiBadge v-if="version.is_active" variant="default">active</UiBadge>
                        <span class="pe-version-date">{{ new Date(version.created_at).toLocaleString() }}</span>
                      </div>
                      <p class="pe-version-summary">{{ version.change_summary }}</p>
                    </div>
                    <div class="pe-version-actions">
                      <UiButton v-if="!version.is_active" size="sm" variant="ghost" @click="rollbackVersion(version.version)"><Undo2 :size="14" /><span>{{ t('settings.rollbackAction') }}</span></UiButton>
                    </div>
                  </div>
                </div>
              </template>
            </UiCard>
          </template>
        </main>
      </div>

      <!-- 上下文预览 -->
      <UiCard class="pe-preview-card">
        <template #content>
          <div class="pe-section-head">
            <div class="pe-section-title"><Eye :size="14" /><span>{{ t('settings.contextPreviewTitle') }}</span></div>
            <UiBadge v-if="promptPreview" variant="outline">{{ promptPreview.estimated_tokens }} tokens</UiBadge>
          </div>
          <div class="ac-preview-fields">
            <div><UiLabel>{{ t('settings.agentIdField') }}</UiLabel><UiInput v-model="promptPreviewAgentId" placeholder="meeting" /></div>
            <div><UiLabel>{{ t('settings.modeFieldOptional') }}</UiLabel><UiInput v-model="promptPreviewMode" placeholder="plan-first" /></div>
            <div><UiLabel>{{ t('settings.sessionIdOptional') }}</UiLabel><UiInput v-model="promptPreviewSessionId" placeholder="session_id" /></div>
          </div>
          <UiButton size="sm" :disabled="previewBusy" @click="generatePromptPreview"><Eye :size="14" /><span>{{ t('settings.generatePreview') }}</span></UiButton>
          <div v-if="promptPreview" class="ac-preview-output">
            <textarea :value="promptPreview.system_prompt" class="pe-content-textarea" rows="6" readonly></textarea>
          </div>
        </template>
      </UiCard>
    </template>
  </section>
</template>

<style scoped>
.agent-prompts-panel {
  display: grid;
  gap: 12px;
}
.ac-prompt-counts {
  display: flex;
  gap: 6px;
}
.ac-prompt-toolbar {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  align-items: center;
}
.ac-pipeline-picker {
  display: flex;
  gap: 8px;
  align-items: center;
}
.ac-pipeline-select {
  min-width: 220px;
}
.ac-pipeline-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
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
  margin-top: 8px;
}
.ac-preview-fields {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 8px;
  margin-bottom: 10px;
}
.ac-preview-output {
  margin-top: 10px;
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
}
.pe-metrics {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
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
.pe-content-textarea {
  width: 100%;
  font-family: ui-monospace, monospace;
  font-size: 12px;
  padding: 8px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-chrome);
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
.pe-empty {
  font-size: 12px;
  padding: 12px;
}
.pe-empty-sm {
  font-size: 12px;
}
.quiet {
  color: var(--text-muted);
}
</style>
