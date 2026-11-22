<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ShieldAlert, Wrench } from '@lucide/vue'
import { UiBadge, UiInput } from '@/components/ui'
import type { ToolDescriptorDto } from '@/api'
import { useStatusLabels } from '@/lib/statusLabels'
import { consumeRequest, pendingToolId } from '@/lib/pageRequests'
import { capabilityReason, type ToolCapabilities } from '@/settings/toolSettings'

const props = defineProps<{
  tools: ToolDescriptorDto[]
  capabilities: ToolCapabilities | null
  allowedToolIds: string[] | null
  loading?: boolean
}>()

const { t, te } = useI18n()
const { riskLabel } = useStatusLabels()

type CategoryKey = 'all' | 'files' | 'shell' | 'search' | 'git' | 'web' | 'virtual'

const search = ref('')
const category = ref<CategoryKey>('all')
const highlightedToolId = ref('')

function classifyTool(id: string): Exclude<CategoryKey, 'all'> {
  if (['read_file', 'ls', 'stat', 'write_file', 'replace_lines', 'replace_bytes', 'insert_line', 'insert_bytes', 'insert_byte', 'delete_line', 'delete_bytes'].includes(id)) return 'files'
  if (['shell', 'command_run', 'sandbox_status', 'sandbox_reset'].includes(id)) return 'shell'
  if (id.includes('search') || id === 'grep' || id === 'glob') return 'search'
  if (id.startsWith('git_')) return 'git'
  if (id.startsWith('web_') || id.startsWith('http_')) return 'web'
  return 'virtual'
}

consumeRequest(pendingToolId, (id) => {
  highlightedToolId.value = id
})

const filteredTools = computed(() => {
  const q = search.value.trim().toLowerCase()
  return props.tools.filter((tool) => {
    if (category.value !== 'all' && classifyTool(tool.id) !== category.value) return false
    if (!q) return true
    return tool.id.toLowerCase().includes(q) || (tool.description || '').toLowerCase().includes(q)
  })
})

const selectedTool = computed<Partial<ToolDescriptorDto> & { id: string } | null>(() => {
  const targetId = highlightedToolId.value
  if (targetId) {
    return props.tools.find(item => item.id === targetId) ?? { id: targetId, description: '' }
  }
  return filteredTools.value[0] ?? null
})

const categories: ReadonlyArray<{ key: CategoryKey; labelKey: string }> = [
  { key: 'all', labelKey: 'toolsSettings.center.builtin.categoryAll' },
  { key: 'files', labelKey: 'toolsSettings.center.builtin.categoryFiles' },
  { key: 'shell', labelKey: 'toolsSettings.center.builtin.categoryShell' },
  { key: 'search', labelKey: 'toolsSettings.center.builtin.categorySearch' },
  { key: 'git', labelKey: 'toolsSettings.center.builtin.categoryGit' },
  { key: 'web', labelKey: 'toolsSettings.center.builtin.categoryWeb' },
  { key: 'virtual', labelKey: 'toolsSettings.center.builtin.categoryVirtual' },
]

function isGranted(id: string): boolean {
  if (!props.allowedToolIds) return true
  return props.allowedToolIds.includes(id)
}

const capabilityText = computed(() => {
  const cap = props.capabilities?.capabilities
  if (!cap) return capabilityReason(props.capabilities ? { reason: props.capabilities.reason ?? undefined } : undefined, { t, te }) || ''
  const rg = cap.ripgrep?.available ? t('toolsSettings.detected') : t('toolsSettings.unavailable')
  const sb = cap.sandbox?.supported ? t('toolsSettings.detected') : t('toolsSettings.unavailable')
  return t('toolsSettings.capabilitySummary', { rg, sandbox: sb })
})
</script>

<template>
  <div class="tool-center-panel" data-testid="builtin-tools-panel">
    <div v-if="capabilityText" class="tool-center-capability-banner">
      <Wrench :size="15" />
      <span>{{ capabilityText }}</span>
      <UiBadge variant="outline">{{ t('toolsSettings.center.builtin.countBadge', { count: tools.length }) }}</UiBadge>
    </div>

    <div class="tool-center-toolbar">
      <div class="tool-center-toolbar-left">
        <UiInput
          v-model="search"
          class="tool-center-search"
          :placeholder="t('toolsSettings.center.builtin.searchPlaceholder')"
        />
        <div class="tool-center-chip-row">
          <button
            v-for="cat in categories"
            :key="cat.key"
            type="button"
            class="tool-center-filter-chip"
            :class="{ active: category === cat.key }"
            @click="category = cat.key"
          >
            {{ t(cat.labelKey) }}
          </button>
        </div>
      </div>
    </div>

    <!-- Single active/discovered detail card: preserves .tool-discovery-card contract for spotlight/search navigation. -->
    <div v-if="selectedTool" class="tool-discovery-card tool-center-discovery-box">
      <div class="tool-center-card-head">
        <div class="tool-center-card-title-group">
          <strong class="tool-center-card-title">{{ selectedTool.id }}</strong>
          <UiBadge v-if="selectedTool.risk" variant="outline">{{ riskLabel(selectedTool.risk) }}</UiBadge>
          <UiBadge v-if="selectedTool.requires_approval" variant="outline">
            <ShieldAlert :size="12" />
            {{ t('toolsSettings.center.builtin.requiresApproval') }}
          </UiBadge>
          <UiBadge v-if="selectedTool.mutates_workspace === false" variant="outline">{{ t('toolsSettings.center.builtin.readonly') }}</UiBadge>
          <UiBadge v-if="!isGranted(selectedTool.id)" variant="outline">{{ t('toolsSettings.notGranted') }}</UiBadge>
        </div>
      </div>
      <p v-if="selectedTool.description" class="setting-desc">{{ selectedTool.description }}</p>
    </div>

    <div v-if="loading && !tools.length" class="tool-center-empty">
      {{ t('common.loading') }}
    </div>
    <div v-else-if="!filteredTools.length" class="tool-center-empty">
      {{ t('toolsSettings.center.builtin.empty') }}
    </div>
    <div v-else class="tool-center-builtin-table-wrap">
      <table class="tool-center-builtin-table">
        <tbody>
          <tr
            v-for="tool in filteredTools"
            :key="tool.id"
            class="tool-builtin-row"
            :class="{ active: selectedTool?.id === tool.id }"
            @click="highlightedToolId = tool.id"
          >
            <td class="tool-builtin-id">{{ tool.id }}</td>
            <td class="tool-builtin-desc">{{ tool.description || '—' }}</td>
            <td class="tool-builtin-badges">
              <UiBadge v-if="tool.risk" variant="outline">{{ riskLabel(tool.risk) }}</UiBadge>
              <UiBadge v-if="tool.requires_approval" variant="outline">{{ t('toolsSettings.center.builtin.requiresApproval') }}</UiBadge>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
