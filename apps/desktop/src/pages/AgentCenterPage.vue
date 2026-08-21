<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { api, type AgentDefinitionDto, type AgentModeTopologyDto, type PromptPipelineDto, type AgentCandidateDto, type AgentRuntimeInstanceDto, type AgentModeNodeDto, type AgentModeEdgeDto } from '@/api'
import { generatedApi } from '@/generated/client'
import { useNotifications } from '@/composables/useNotifications'
import { UiButton, UiCard, UiBadge, UiInput, UiLabel, UiSwitch, UiTextarea, UiSelect } from '@/components/ui'
import AgentModeCanvas from '@/components/canvas/AgentModeCanvas.vue'

const { notify } = useNotifications()

type TabKey = 'agents' | 'modes' | 'prompts' | 'candidates' | 'instances'
const activeTab = ref<TabKey>('agents')

// ── agents ──
const agents = ref<AgentDefinitionDto[]>([])
const agentDraft = ref<Partial<AgentDefinitionDto> & { etag?: string | null }>({ name: '', layer: 'operation', agent_type: 'assistant', description: '', allowed_tools: [], capabilities: [], system_prompt: '', enabled: true })
const editingAgentId = ref<string | null>(null)
const agentVersions = ref<Record<string, unknown>[]>([])
const showAgentForm = ref(false)
const promotePickModeId = ref('')
const candidateToPromote = ref<string | null>(null)

async function loadAgents() {
  try { const list = await api.listAgentDefinitions(); agents.value = Array.isArray(list) ? list : (list as unknown as { data: AgentDefinitionDto[] })?.data ?? [] } catch (e) { notify.error(e, { title: '加载智能体失败' }) }
}
function openCreateAgent() {
  editingAgentId.value = null
  agentDraft.value = { name: '', layer: 'operation', agent_type: 'assistant', description: '', allowed_tools: [], capabilities: [], system_prompt: '', enabled: true, etag: null }
  showAgentForm.value = true
}
function openEditAgent(a: AgentDefinitionDto) {
  editingAgentId.value = a.id
  agentDraft.value = { ...a, allowed_tools: [...(a.allowed_tools ?? [])], capabilities: [...(a.capabilities ?? [])], etag: (a as unknown as { etag?: string }).etag ?? (a.revision != null ? String(a.revision) : null) }
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
      notify.success({ title: '已保存草稿', message: updated.name })
    } else {
      const created = await api.createAgentDraft(body)
      notify.success({ title: '已创建草稿', message: created.name })
    }
    showAgentForm.value = false
    await loadAgents()
  } catch (e) { notify.error(e, { title: '保存失败' }) }
}
async function publishAgent(id: string) {
  try { await api.publishAgent(id); notify.success({ title: '已发布', message: id }); await loadAgents() } catch (e) { notify.error(e) }
}
async function archiveAgent(id: string) {
  try { await api.archiveAgent(id); notify.success({ message: '已归档' }); await loadAgents() } catch (e) { notify.error(e) }
}
async function fetchAgentVersions(id: string) {
  try { agentVersions.value = await api.listAgentVersions(id) as unknown as Record<string, unknown>[] } catch (e) { notify.error(e) }
}

// ── modes ──
const modes = ref<AgentModeTopologyDto[]>([])
const selectedModeId = ref<string | null>(null)
const modeNodes = ref<AgentModeNodeDto[]>([])
const modeEdges = ref<AgentModeEdgeDto[]>([])
const modeEtag = ref<string | null>(null)
const selectedModeNode = ref<AgentModeNodeDto | null>(null)
const nodeOverride = ref<{ agent_id: string; label: string }>({ agent_id: '', label: '' })

const selectedMode = computed(() => modes.value.find((m) => m.id === selectedModeId.value) ?? null)

async function loadModes() {
  try {
    const list = await api.listAgentModeTopologies()
    modes.value = Array.isArray(list) ? list : []
    if (!selectedModeId.value && modes.value[0]) selectMode(modes.value[0].id)
  } catch (e) { notify.error(e, { title: '加载模式失败' }) }
}
async function selectMode(id: string) {
  selectedModeId.value = id
  try {
    const m = await api.getAgentModeTopology(id)
    modeNodes.value = (m.nodes ?? []) as AgentModeNodeDto[]
    modeEdges.value = (m.edges ?? []) as AgentModeEdgeDto[]
    modeEtag.value = (m as unknown as { etag?: string }).etag ?? (m.revision != null ? String(m.revision) : null)
  } catch { modeNodes.value = []; modeEdges.value = []; modeEtag.value = null }
}
async function createMode() {
  try {
    const m = await api.createAgentModeDraft({ display_name: `mode-${Date.now().toString(36).slice(0,6)}`, summary: 'draft', nodes: [], edges: [], canvas_layout: {} })
    await loadModes(); selectedModeId.value = m.id
  } catch (e) { notify.error(e) }
}
async function saveModeDraft() {
  if (!selectedModeId.value) return
  try {
    await api.updateAgentModeDraft(selectedModeId.value, { nodes: modeNodes.value, edges: modeEdges.value, canvas_layout: {} }, modeEtag.value)
    notify.success({ title: '模式草稿已保存', message: '需重发布生效' })
    await loadModes()
  } catch (e) { notify.error(e, { title: '保存模式失败' }) }
}
async function publishMode() {
  if (!selectedModeId.value) return
  try { await api.publishAgentMode(selectedModeId.value); notify.success({ message: '模式已发布' }); await loadModes() } catch (e) { notify.error(e) }
}
function addModeNode() {
  const id = `n-${Date.now().toString(36)}`
  const firstAgent = agents.value[0]?.id ?? 'agent_meeting'
  modeNodes.value = [...modeNodes.value, { id, agent_id: firstAgent, lane: 'operation', position: { x: 80 + modeNodes.value.length * 40, y: 80 }, label: `node ${modeNodes.value.length + 1}` }]
}
function handleSelectNode(n: AgentModeNodeDto | null) {
  selectedModeNode.value = n
  if (n) nodeOverride.value = { agent_id: n.agent_id, label: String(n.label ?? '') }
}
function applyNodeOverride() {
  if (!selectedModeNode.value) return
  modeNodes.value = modeNodes.value.map((n) => n.id === selectedModeNode.value!.id ? { ...n, agent_id: nodeOverride.value.agent_id, label: nodeOverride.value.label } : n)
  notify.success({ message: '节点覆盖已更新（未保存）' })
}

// ── pipelines ──
const pipelines = ref<PromptPipelineDto[]>([])
const pipelineDraft = ref<Partial<PromptPipelineDto>>({ name: '', description: '', scope: 'global' })
const editingPipelineId = ref<string | null>(null)
const showPipelineForm = ref(false)

async function loadPipelines() {
  try { const list = await api.listPromptPipelines(); pipelines.value = Array.isArray(list) ? list : [] } catch (e) { notify.error(e) }
}
function openPipelineCreate() { editingPipelineId.value = null; pipelineDraft.value = { name: '', description: '', scope: 'global' }; showPipelineForm.value = true }
function openPipelineEdit(p: PromptPipelineDto) { editingPipelineId.value = p.id; pipelineDraft.value = { ...p }; showPipelineForm.value = true }
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
  } catch (e) { notify.error(e) }
}
async function publishPipeline(id: string) { try { await api.publishPromptPipeline(id); notify.success({ message: '已发布' }); await loadPipelines() } catch (e) { notify.error(e) } }

// ── candidates ──
const candidates = ref<AgentCandidateDto[]>([])
const candidateModePick = ref('')

async function loadCandidates() { try { const list = await api.listCandidates(); candidates.value = Array.isArray(list) ? list : [] } catch (e) { notify.error(e) } }
async function promoteCandidate(id: string) {
  if (!candidateModePick.value) { notify.error({ message: '请选择目标 mode 草稿' }); return }
  try {
    await api.promoteCandidate(id, { target_mode_draft_id: candidateModePick.value })
    notify.success({ title: '已晋升', message: '已加入 mode 草稿，需重发布' })
    await Promise.all([loadAgents(), loadCandidates(), loadModes()])
  } catch (e) { notify.error(e, { title: '晋升失败' }) }
}
async function rejectCandidate(id: string) { try { await api.rejectCandidate(id); notify.success({ message: '已拒绝' }); await loadCandidates() } catch (e) { notify.error(e) } }

// ── runtime instances ──
const runtimeRunId = ref('')
const runtimeInstances = ref<AgentRuntimeInstanceDto[]>([])
let runtimeTimer: number | null = null

async function loadRuntimeInstances() {
  try { runtimeInstances.value = await api.listRuntimeInstances(runtimeRunId.value || undefined) } catch (e) { notify.error(e) }
}
function startRuntimePoll() { stopRuntimePoll(); loadRuntimeInstances(); runtimeTimer = window.setInterval(loadRuntimeInstances, 3000) }
function stopRuntimePoll() { if (runtimeTimer) { clearInterval(runtimeTimer); runtimeTimer = null } }
async function controlInstance(runId: string, action: 'pause'|'resume'|'cancel') {
  try { await generatedApi.controlRun(runId, { action }); notify.success({ message: `${action} 已发送` }); await loadRuntimeInstances() } catch (e) { notify.error(e) }
}
async function reassignQueued(targetRunId: string) {
  // reassign uses interactions endpoint; for demo we require session context via prompt
  const sessionId = window.prompt('session_id for reassign?')?.trim()
  const interactionId = window.prompt('interaction_id?')?.trim()
  if (!sessionId || !interactionId) return
  try { await api.reassignInteraction(sessionId, interactionId, { target_run_id: targetRunId }); notify.success({ message: '已改派' }) } catch (e) { notify.error(e) }
}

onMounted(() => {
  loadAgents(); loadModes(); loadPipelines(); loadCandidates()
})
</script>

<template>
  <div class="agent-center-page">
    <header class="agent-center-head">
      <h1>智能体中心</h1>
      <div class="tabs">
        <button :class="{ active: activeTab==='agents' }" @click="activeTab='agents'">智能体</button>
        <button :class="{ active: activeTab==='modes' }" @click="activeTab='modes'">智能体模式</button>
        <button :class="{ active: activeTab==='prompts' }" @click="activeTab='prompts'">提示词引擎</button>
        <button :class="{ active: activeTab==='candidates' }" @click="activeTab='candidates'">候选智能体</button>
        <button :class="{ active: activeTab==='instances' }" @click="activeTab='instances'">运行实例</button>
      </div>
    </header>

    <!-- 智能体 -->
    <section v-if="activeTab==='agents'" class="tab-panel">
      <div class="panel-head">
        <h2>智能体</h2>
        <div class="actions">
          <UiButton size="sm" @click="openCreateAgent">新增草稿</UiButton>
          <UiButton size="sm" variant="outline" @click="loadAgents">刷新</UiButton>
        </div>
      </div>
      <div class="card-grid">
        <UiCard v-for="a in agents" :key="a.id" class="agent-card">
          <template #content>
            <div class="card-title">{{ a.name }} <UiBadge variant="outline">{{ a.layer }}</UiBadge> <UiBadge v-if="a.is_built_in" variant="secondary">内置</UiBadge></div>
            <div class="card-desc">{{ a.description || '—' }}</div>
            <div class="card-meta">{{ a.agent_type }} · {{ (a.allowed_tools ?? []).join(', ') || 'no tools' }} · rev {{ a.revision ?? '—' }}</div>
            <div class="card-actions">
              <UiButton size="xs" variant="outline" @click="openEditAgent(a)">编辑</UiButton>
              <UiButton size="xs" @click="publishAgent(a.id)">发布</UiButton>
              <UiButton size="xs" variant="outline" @click="archiveAgent(a.id)">归档</UiButton>
              <UiButton size="xs" variant="ghost" @click="fetchAgentVersions(a.id)">版本</UiButton>
            </div>
          </template>
        </UiCard>
      </div>
      <div v-if="agentVersions.length" class="version-list">
        <h3>版本</h3>
        <div v-for="v in agentVersions" :key="String((v as Record<string,unknown>).id)" class="version-row">{{ JSON.stringify(v).slice(0,180) }}</div>
      </div>
      <div v-if="showAgentForm" class="drawer">
        <UiCard>
          <template #content>
            <h3>{{ editingAgentId ? '编辑草稿' : '新建草稿' }}</h3>
            <div class="form-grid">
              <div><UiLabel>名称</UiLabel><UiInput v-model="agentDraft.name as string" /></div>
              <div><UiLabel>层</UiLabel><UiSelect v-model="agentDraft.layer as string"><option value="operation">operation</option><option value="execution">execution</option></UiSelect></div>
              <div><UiLabel>类型</UiLabel><UiInput v-model="agentDraft.agent_type as string" /></div>
              <div><UiLabel>描述</UiLabel><UiTextarea v-model="agentDraft.description as string" :rows="2" /></div>
              <div><UiLabel>allowed_tools (逗号分隔)</UiLabel><UiInput :model-value="(agentDraft.allowed_tools ?? []).join(',')" @update:model-value="(v:string)=> agentDraft.allowed_tools = v.split(',').map(s=>s.trim()).filter(Boolean)" /></div>
              <div><UiLabel>capabilities (逗号分隔)</UiLabel><UiInput :model-value="(agentDraft.capabilities ?? []).join(',')" @update:model-value="(v:string)=> agentDraft.capabilities = v.split(',').map(s=>s.trim()).filter(Boolean)" /></div>
              <div class="full"><UiLabel>system_prompt</UiLabel><UiTextarea v-model="agentDraft.system_prompt as string" :rows="4" /></div>
              <div><UiLabel>启用</UiLabel><UiSwitch v-model="agentDraft.enabled as boolean" /></div>
              <div v-if="agentDraft.etag"><UiLabel>etag / revision</UiLabel><UiInput v-model="agentDraft.etag as string" /></div>
            </div>
            <div class="form-actions">
              <UiButton size="sm" @click="saveAgentDraft">保存草稿</UiButton>
              <UiButton size="sm" variant="outline" @click="showAgentForm=false">取消</UiButton>
            </div>
            <div v-if="candidateToPromote" class="promote-hint">候选晋升将创建正式 agent 草稿并建议加入来源 mode 草稿，需重发布 mode</div>
          </template>
        </UiCard>
      </div>
    </section>

    <!-- 智能体模式 -->
    <section v-if="activeTab==='modes'" class="tab-panel">
      <div class="panel-head">
        <h2>智能体模式 · 双泳道</h2>
        <div class="actions">
          <UiButton size="sm" @click="createMode">新建模式</UiButton>
          <UiButton size="sm" variant="outline" @click="loadModes">刷新</UiButton>
          <UiButton size="sm" :disabled="!selectedModeId" @click="saveModeDraft">保存草稿</UiButton>
          <UiButton size="sm" :disabled="!selectedModeId" @click="publishMode">发布</UiButton>
          <UiButton size="sm" variant="ghost" @click="addModeNode">新增节点</UiButton>
        </div>
      </div>
      <div class="mode-picker">
        <UiLabel>选择模式</UiLabel>
        <select v-model="selectedModeId" class="settings-select" @change="selectedModeId && selectMode(selectedModeId)">
          <option v-for="m in modes" :key="m.id" :value="m.id">{{ m.display_name }} · {{ m.id.slice(0,8) }} · {{ m.status ?? 'draft' }}</option>
        </select>
        <span v-if="selectedMode" class="mode-hint">rev {{ selectedMode.revision ?? '—' }} · {{ modeNodes.length }} 节点 · {{ modeEdges.length }} 边</span>
      </div>
      <AgentModeCanvas
        :nodes="modeNodes"
        :edges="modeEdges"
        :agents="agents"
        @update:nodes="modeNodes = $event"
        @update:edges="modeEdges = $event"
        @select-node="handleSelectNode"
      />
      <!-- 独立面板：节点覆盖编辑 -->
      <UiCard v-if="selectedModeNode" class="node-override-panel">
        <template #content>
          <h3>节点覆盖 · {{ selectedModeNode.id }}</h3>
          <p class="hint">仅覆盖当前模式节点的 agent 绑定，不改全局智能体配置</p>
          <div class="form-grid">
            <div><UiLabel>agent_id</UiLabel>
              <select v-model="nodeOverride.agent_id" class="settings-select">
                <option v-for="a in agents" :key="a.id" :value="a.id">{{ a.name }} ({{ a.id.slice(0,6) }})</option>
              </select>
            </div>
            <div><UiLabel>标签</UiLabel><UiInput v-model="nodeOverride.label" /></div>
          </div>
          <div class="form-actions"><UiButton size="sm" @click="applyNodeOverride">应用覆盖</UiButton></div>
        </template>
      </UiCard>
    </section>

    <!-- 提示词引擎 -->
    <section v-if="activeTab==='prompts'" class="tab-panel">
      <div class="panel-head"><h2>提示词引擎</h2><div class="actions"><UiButton size="sm" @click="openPipelineCreate">新建</UiButton><UiButton size="sm" variant="outline" @click="loadPipelines">刷新</UiButton></div></div>
      <div class="card-grid">
        <UiCard v-for="p in pipelines" :key="p.id">
          <template #content>
            <div class="card-title">{{ p.name ?? p.title ?? p.key ?? p.id.slice(0,8) }} <UiBadge variant="outline">{{ p.status ?? 'draft' }}</UiBadge></div>
            <div class="card-desc">{{ p.description ?? '—' }}</div>
            <div class="card-actions"><UiButton size="xs" variant="outline" @click="openPipelineEdit(p)">编辑</UiButton><UiButton size="xs" @click="publishPipeline(p.id)">发布</UiButton></div>
          </template>
        </UiCard>
      </div>
      <UiCard v-if="showPipelineForm">
        <template #content>
          <h3>{{ editingPipelineId ? '编辑' : '新建' }}提示词管线</h3>
          <div class="form-grid">
            <div><UiLabel>名称</UiLabel><UiInput v-model="pipelineDraft.name as string" /></div>
            <div><UiLabel>描述</UiLabel><UiInput v-model="pipelineDraft.description as string" /></div>
            <div><UiLabel>scope</UiLabel><UiInput v-model="pipelineDraft.scope as string" /></div>
          </div>
          <div class="form-actions"><UiButton size="sm" @click="savePipelineDraft">保存草稿</UiButton><UiButton size="sm" variant="outline" @click="showPipelineForm=false">取消</UiButton></div>
          <div class="hint">画布预览复用模式页基建：保存后可在智能体模式画布中引用此管线</div>
        </template>
      </UiCard>
    </section>

    <!-- 候选智能体 -->
    <section v-if="activeTab==='candidates'" class="tab-panel">
      <div class="panel-head"><h2>候选智能体</h2><div class="actions"><UiButton size="sm" variant="outline" @click="loadCandidates">刷新</UiButton></div></div>
      <div v-if="candidates.length===0" class="empty">暂无候选</div>
      <div v-for="c in candidates" :key="c.id" class="candidate-row">
        <div><strong>{{ c.name }}</strong> · {{ c.layer }} · {{ c.agent_type }} · {{ c.status }}</div>
        <div class="candidate-desc">{{ c.description }}</div>
        <div class="candidate-tools">建议工具: {{ (c.suggested_tools ?? []).join(', ') || '—' }}</div>
        <div class="candidate-actions">
          <select v-model="candidateModePick" class="settings-select" style="min-width: 220px;">
            <option value="">选择目标 mode 草稿</option>
            <option v-for="m in modes" :key="m.id" :value="m.id">{{ m.display_name }} · {{ m.id.slice(0,8) }}</option>
          </select>
          <UiButton size="xs" @click="promoteCandidate(c.id)">晋升</UiButton>
          <UiButton size="xs" variant="outline" @click="rejectCandidate(c.id)">拒绝</UiButton>
        </div>
        <div class="promote-hint">晋升将创建正式 agent 草稿并加入所选 mode 草稿，需重发布 mode</div>
      </div>
    </section>

    <!-- 运行实例 -->
    <section v-if="activeTab==='instances'" class="tab-panel">
      <div class="panel-head"><h2>运行实例</h2><div class="actions">
        <UiInput v-model="runtimeRunId" placeholder="run_id 过滤（可选）" style="width: 260px;" />
        <UiButton size="sm" variant="outline" @click="loadRuntimeInstances">查询</UiButton>
        <UiButton size="sm" @click="startRuntimePoll">轮询</UiButton>
        <UiButton size="sm" variant="ghost" @click="stopRuntimePoll">停止</UiButton>
      </div></div>
      <div v-if="runtimeInstances.length===0" class="empty">无实例 · 输入 run_id 查询或轮询</div>
      <div v-for="inst in runtimeInstances" :key="inst.id" class="instance-row">
        <div><strong>{{ inst.agent_name ?? inst.agent_id ?? inst.id.slice(0,8) }}</strong> · {{ inst.status }} · run {{ inst.run_id.slice(0,8) }}</div>
        <div class="instance-actions">
          <UiButton size="xs" variant="outline" @click="controlInstance(inst.run_id,'pause')">暂停</UiButton>
          <UiButton size="xs" variant="outline" @click="controlInstance(inst.run_id,'resume')">恢复</UiButton>
          <UiButton size="xs" variant="destructive" @click="controlInstance(inst.run_id,'cancel')">取消</UiButton>
          <UiButton size="xs" variant="ghost" @click="reassignQueued(inst.run_id)">队列改派</UiButton>
          <UiButton size="xs" variant="ghost" @click="notify.success({ message: '插入需在发送框选择目标 run' })">插入控制</UiButton>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.agent-center-page { display: grid; gap: 16px; padding: 16px; max-width: 1200px; margin: 0 auto; }
.agent-center-head { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 12px; }
.agent-center-head h1 { font-size: 20px; font-weight: 700; }
.tabs { display: flex; gap: 6px; flex-wrap: wrap; }
.tabs button { padding: 6px 12px; border-radius: 999px; border: 1px solid var(--border-muted); background: var(--surface-section); font-size: 13px; }
.tabs button.active { background: var(--primary); color: var(--primary-foreground); border-color: var(--primary); }
.tab-panel { display: grid; gap: 12px; }
.panel-head { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
.panel-head h2 { font-size: 16px; font-weight: 700; }
.actions { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.card-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 12px; }
.agent-card :deep(.card-content) { display: grid; gap: 6px; }
.card-title { font-weight: 600; display: flex; gap: 6px; align-items: center; flex-wrap: wrap; }
.card-desc { font-size: 12px; color: var(--text-muted); }
.card-meta { font-size: 11px; color: var(--text-muted); }
.card-actions { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 4px; }
.version-row { font-size: 11px; color: var(--text-muted); border: 1px solid var(--border-muted); border-radius: 6px; padding: 6px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.drawer { margin-top: 12px; }
.form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; margin-top: 12px; }
.form-grid .full { grid-column: 1 / -1; }
.form-actions { display: flex; gap: 8px; margin-top: 12px; }
.mode-picker { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
.settings-select { height: 36px; border: 1px solid var(--border-muted); border-radius: 8px; padding: 0 12px; background: var(--surface-raised); font-size: 13px; }
.mode-hint { font-size: 11px; color: var(--text-muted); }
.node-override-panel { border: 1px solid var(--border-muted); }
.hint { font-size: 11px; color: var(--text-muted); margin: 6px 0; }
.candidate-row, .instance-row { border: 1px solid var(--border-muted); border-radius: 10px; padding: 12px; display: grid; gap: 6px; background: var(--surface-section); }
.candidate-desc, .candidate-tools { font-size: 12px; color: var(--text-muted); }
.candidate-actions, .instance-actions { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.promote-hint { font-size: 11px; color: var(--text-muted); background: var(--surface-raised); padding: 6px 8px; border-radius: 6px; }
.empty { color: var(--text-muted); font-size: 13px; padding: 12px; border: 1px dashed var(--border-muted); border-radius: 8px; }
</style>

