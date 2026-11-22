<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Info, Plus, Search, ShieldCheck } from '@lucide/vue'
import type { AgentViewDto, HarnessManifestDto, ToolDescriptorDto } from '@/api'
import { useStatusLabels } from '@/lib/statusLabels'
import { requestToolAgent } from '@/lib/pageRequests'
import { manifestTools } from '@/toolCatalog'
import { UiBadge, UiButton, UiInput, UiSelectField } from '@/components/ui'

const props = defineProps<{
  configuringAgent: AgentViewDto
  configuringIsReadOnly: boolean
  availableTools: ToolDescriptorDto[]
  harnessManifest: HarnessManifestDto | null
  editTools: string[]
  editCapabilities: string[]
  pendingGovernanceCount: number
}>()

const emit = defineEmits<{
  'update:editTools': [tools: string[]]
  'update:editCapabilities': [caps: string[]]
}>()

const { t } = useI18n()
const { riskLabel } = useStatusLabels()

const toolQuery = ref('')
const toolSourceFilter = ref('all')
const toolRiskFilter = ref('all')
const newCapability = ref('')

const manifestToolList = computed(() => manifestTools(props.harnessManifest, props.availableTools))
const hasWildcardTools = computed(() => props.editTools.includes('*'))

function isToolSelected(toolId: string): boolean {
  return hasWildcardTools.value || props.editTools.includes(toolId)
}

const filteredAgentTools = computed(() => {
  const q = toolQuery.value.trim().toLowerCase()
  return manifestToolList.value.filter((tool) => {
    if (toolSourceFilter.value !== 'all' && tool.source !== toolSourceFilter.value) return false
    if (toolRiskFilter.value !== 'all' && tool.risk !== toolRiskFilter.value) return false
    if (!q) return true
    return [tool.id, tool.display_name, tool.domain, tool.source, tool.risk].some((v) => v?.toLowerCase().includes(q))
  })
})

const groupedAgentTools = computed(() => {
  const groups = new Map<string, typeof manifestToolList.value>()
  for (const tool of filteredAgentTools.value) {
    const key = tool.source || 'unknown'
    const list = groups.get(key) ?? []
    list.push(tool)
    groups.set(key, list)
  }
  return [...groups.entries()].sort((a, b) => a[0].localeCompare(b[0]))
})

const toolSourceOptions = computed(() => Array.from(new Set(manifestToolList.value.map((tool) => tool.source))).sort())
const toolRiskOptions = computed(() => Array.from(new Set(manifestToolList.value.map((tool) => tool.risk))).sort())

const toolSelectionSummary = computed(() => {
  const total = manifestToolList.value.length
  if (hasWildcardTools.value) {
    const approval = manifestToolList.value.filter((item) => item.requires_approval).length
    return { total, selected: total, approval, wildcard: true }
  }
  const selected = props.editTools.length
  const approval = manifestToolList.value.filter((item) => item.requires_approval && props.editTools.includes(item.id)).length
  return { total, selected, approval, wildcard: false }
})

function toggleTool(toolId: string) {
  if (hasWildcardTools.value) {
    emit('update:editTools', manifestToolList.value.map((item) => item.id).filter((id) => id !== toolId))
    return
  }
  const next = [...props.editTools]
  const idx = next.indexOf(toolId)
  if (idx >= 0) next.splice(idx, 1)
  else next.push(toolId)
  emit('update:editTools', next)
}

function removeCapability(cap: string) {
  emit('update:editCapabilities', props.editCapabilities.filter((item) => item !== cap))
}

function addCapability() {
  const cap = newCapability.value.trim()
  if (cap && !props.editCapabilities.includes(cap)) {
    emit('update:editCapabilities', [...props.editCapabilities, cap])
    newCapability.value = ''
  }
}
</script>

<template>
  <div class="agent-config-section" data-testid="agent-section-tools">
    <div class="agent-config-section-title">
      {{ t('settings.agentSectionTools') }}
      <UiBadge v-if="toolSelectionSummary.wildcard" variant="default" data-testid="agent-wildcard-tools-badge">
        {{ t('settings.allToolsWildcard', { total: toolSelectionSummary.total }) }}
      </UiBadge>
      <UiBadge v-else variant="outline">
        {{ toolSelectionSummary.selected }}/{{ toolSelectionSummary.total }}
      </UiBadge>
      <UiBadge v-if="toolSelectionSummary.approval > 0" variant="secondary">
        {{ t('settings.approvalRequired') }} {{ toolSelectionSummary.approval }}
      </UiBadge>
    </div>
    <p class="agent-config-hint">{{ t('settings.agentToolsHint') }}</p>
    <UiButton variant="outline" size="sm" @click="requestToolAgent(configuringAgent.id)">
      {{ t('toolsSettings.agentSettingsLink') }}
    </UiButton>
    <div class="agent-tool-toolbar">
      <div class="agent-tool-search">
        <Search :size="14" />
        <UiInput v-model="toolQuery" :placeholder="t('settings.toolSearchPlaceholder')" />
      </div>
      <UiSelectField
        v-model="toolSourceFilter"
        class="compact"
        :aria-label="t('settings.allSources')"
        :options="[
          { value: 'all', label: t('settings.allSources') },
          ...toolSourceOptions.map((src) => ({ value: src, label: src })),
        ]"
      />
      <UiSelectField
        v-model="toolRiskFilter"
        class="compact"
        :aria-label="t('settings.allRisks')"
        :options="[
          { value: 'all', label: t('settings.allRisks') },
          ...toolRiskOptions.map((risk) => ({ value: risk, label: risk })),
        ]"
      />
    </div>
    <div v-if="configuringIsReadOnly" class="agent-builtin-hint">
      <Info :size="14" />
      <span>{{ t('settings.builtInCloneHint') }}</span>
    </div>
    <div v-for="[source, tools] in groupedAgentTools" :key="source" class="agent-tool-group">
      <div class="agent-tool-group-head">
        <strong>{{ source }}</strong>
        <UiBadge variant="outline">{{ tools.length }}</UiBadge>
      </div>
      <div class="agent-tool-grid">
        <button
          v-for="tool in tools"
          :key="tool.id"
          type="button"
          class="agent-tool-chip"
          :class="{
            active: isToolSelected(tool.id),
            risky: tool.requires_approval,
          }"
          :disabled="configuringIsReadOnly"
          :title="tool.display_name + ' · ' + riskLabel(tool.risk) + (tool.requires_approval ? ' · ' + t('settings.approvalRequired') : '')"
          @click="toggleTool(tool.id)"
        >
          <span class="agent-tool-name">{{ tool.display_name }}</span>
          <span class="agent-tool-meta">
            <span class="agent-tool-risk" :class="tool.risk">{{ riskLabel(tool.risk) }}</span>
            <span v-if="tool.requires_approval" class="agent-tool-approval">⚑ {{ t('settings.approvalRequired') }}</span>
          </span>
          <span class="agent-tool-id">{{ tool.id }}</span>
        </button>
      </div>
    </div>
    <p v-if="groupedAgentTools.length === 0" class="quiet">{{ t('settings.noTools') }}</p>
    <div class="agent-tool-bulk">
      <UiButton
        variant="ghost"
        size="sm"
        :disabled="configuringIsReadOnly"
        @click="emit('update:editTools', manifestToolList.filter((item) => !item.requires_approval).map((item) => item.id))"
      >
        {{ t('settings.selectReadOnlyTools') }}
      </UiButton>
      <UiButton
        variant="ghost"
        size="sm"
        :disabled="configuringIsReadOnly"
        @click="emit('update:editTools', manifestToolList.map((item) => item.id))"
      >
        {{ t('settings.selectAllReadWrite') }}
      </UiButton>
      <UiButton
        variant="ghost"
        size="sm"
        :disabled="configuringIsReadOnly"
        @click="emit('update:editTools', ['*'])"
      >
        {{ t('settings.allowAllTools') }}
      </UiButton>
      <UiButton
        variant="ghost"
        size="sm"
        :disabled="configuringIsReadOnly"
        @click="emit('update:editTools', [])"
      >
        {{ t('settings.clearSelection') }}
      </UiButton>
    </div>

    <div class="runtime-binding-readonly agent-governance-card">
      <ShieldCheck :size="16" />
      <div>
        <strong>{{ t('settings.agentGovernanceTitle') }}</strong>
        <span>{{ t('settings.agentGovernanceHint') }}</span>
      </div>
      <UiBadge :variant="pendingGovernanceCount > 0 ? 'default' : 'outline'">
        {{ t('settings.pendingGovernance', { count: pendingGovernanceCount }) }}
      </UiBadge>
    </div>

    <div class="agent-config-section-title secondary">{{ t('settings.agentCapabilities') }}</div>
    <p class="agent-config-hint">{{ t('settings.agentCapabilitiesHint') }}</p>
    <div class="agent-capability-list">
      <span v-for="cap in editCapabilities" :key="cap" class="agent-cap-tag">
        {{ cap }}
        <button type="button" class="agent-cap-remove" :disabled="configuringIsReadOnly" @click="removeCapability(cap)">×</button>
      </span>
    </div>
    <div class="agent-cap-add-row">
      <UiInput
        v-model="newCapability"
        :disabled="configuringIsReadOnly"
        :placeholder="t('settings.newCapabilityPlaceholder')"
        @keydown.enter="addCapability"
      />
      <UiButton variant="outline" size="sm" :disabled="configuringIsReadOnly || !newCapability.trim()" @click="addCapability">
        <Plus :size="14" />
        {{ t('settings.addCapability') }}
      </UiButton>
    </div>
  </div>
</template>
