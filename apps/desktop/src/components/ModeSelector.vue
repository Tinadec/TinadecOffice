<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick } from 'vue'
import { ChevronDown, Moon } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { api, type AgentModeTopologyDto } from '@/api'
import { modeIcon, sortModes } from '@/lib/modePresentation'

const { t } = useI18n()
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
const panelStyle = computed(() => getPanelStyle())
const panelDataAttrs = computed(() => getPanelDataAttributes())

const props = defineProps<{
  /** Published ModeVersion id; null = follow the session's binding or the workspace default. */
  modeVersionId?: string | null
}>()

const emit = defineEmits<{
  'update:modeVersionId': [value: string | null]
}>()

const showDropdown = ref(false)
const triggerRef = ref<HTMLElement | null>(null)
const dropdownStyle = ref<Record<string, string>>({})

// 模式列表唯一来源 = 已安装包发布的模式（GET /agent-modes）。
// 六值 agent_mode 词表已从契约删除：Core 只认 mode_version_id，列表里不再有
// 与包无关的固定项，也不做「对话模式/拓扑」分组——那两组本来就是同一批包模式。
const modeVersions = ref<AgentModeTopologyDto[]>([])

const availableVersions = computed(() => {
  const seen = new Set<string>()
  return sortModes(modeVersions.value.filter((v) => {
    if (v.status !== 'published' || !v.latest_published_mode_version_id) return false
    // 同一模式可能存在多行 published（运维/夹具安装各一条，Core 把去重责任下放给
    // 客户端）；按用户可见的 display_name 去重——两行名字相同，用户就分不出来。
    const key = v.display_name.trim().toLowerCase()
    if (seen.has(key)) return false
    seen.add(key)
    return true
  }))
})

const selectedVersion = computed(() =>
  availableVersions.value.find(v => v.latest_published_mode_version_id === props.modeVersionId) ?? null
)
// 已选 version 失效（被禁用/卸载/跨工作区残留）时不再高亮；发送时 mode_version_id
// 仍然带着，服务端会以 pack_disabled / invalid_request 显式失败，绝不静默回落。
const selectionStale = computed(() => !!props.modeVersionId && !selectedVersion.value)
const triggerLabel = computed(() => selectedVersion.value?.display_name ?? t('chat.followDefault'))

function versionSummary(version: AgentModeTopologyDto): string {
  return version.description?.trim() || ''
}

async function loadModeVersions() {
  try {
    const list = await api.listAgentModeTopologies()
    modeVersions.value = Array.isArray(list) ? (list as AgentModeTopologyDto[]) : []
  } catch { /* gateway offline：列表留空，触发器显示「跟随默认」 */ }
}

function selectVersion(versionId: string | null) {
  if (versionId !== null && !availableVersions.value.some(v => v.latest_published_mode_version_id === versionId)) {
    // OpenCode 式防呆：列表里不存在的项根本设置不进去。
    return
  }
  emit('update:modeVersionId', versionId)
  showDropdown.value = false
}

function updateDropdownPosition() {
  const trigger = triggerRef.value
  if (!trigger) return
  const rect = trigger.getBoundingClientRect()
  const vh = window.innerHeight
  const vw = window.innerWidth
  // Wide enough for a two-line description; estH covers seven modes before it scrolls.
  const ddWidth = 340
  const estH = 380
  const spaceBelow = vh - rect.bottom
  const spaceAbove = rect.top
  const flip = spaceBelow < estH && spaceAbove > spaceBelow
  const left = Math.max(8, Math.min(rect.left, vw - ddWidth - 8))
  if (flip) {
    dropdownStyle.value = {
      position: 'fixed',
      bottom: `${vh - rect.top + 6}px`,
      left: `${left}px`,
      minWidth: '240px',
      maxHeight: `${Math.min(440, spaceAbove - 12)}px`,
      overflowY: 'auto',
    }
  } else {
    dropdownStyle.value = {
      position: 'fixed',
      top: `${rect.bottom + 6}px`,
      left: `${left}px`,
      minWidth: '240px',
      maxHeight: `${Math.min(440, spaceBelow - 12)}px`,
      overflowY: 'auto',
    }
  }
}

async function toggleDropdown() {
  showDropdown.value = !showDropdown.value
  if (showDropdown.value) {
    // 每次打开都刷新：智能体中心发布新版本 / 安装或启用 pack 后立即可见。
    void loadModeVersions()
    await nextTick()
    updateDropdownPosition()
  }
}

function handleClickOutside(event: MouseEvent) {
  const target = event.target as HTMLElement
  if (!target.closest('.mode-selector-trigger') && !target.closest('.mode-selector-portal')) {
    showDropdown.value = false
  }
}

onMounted(() => {
  document.addEventListener('click', handleClickOutside)
  void loadModeVersions()
})
onUnmounted(() => document.removeEventListener('click', handleClickOutside))
</script>

<template>
  <div class="mode-selector">
    <button
      ref="triggerRef"
      class="mode-selector-trigger"
      :title="t('chat.modeVersion')"
      @click="toggleDropdown"
    >
      <component :is="selectedVersion ? modeIcon(selectedVersion.slug) : Moon" :size="14" />
      <span class="mode-selector-label">{{ triggerLabel }}</span>
      <span v-if="selectionStale" class="mode-selector-stale" :title="t('chat.modeUnavailable')">⚠</span>
      <ChevronDown :size="12" class="mode-selector-chevron" />
    </button>

    <Teleport to="body">
      <div
        v-if="showDropdown"
        class="mode-selector-portal"
        :style="[dropdownStyle, panelStyle]"
        v-bind="panelDataAttrs"
      >
        <button
          class="mode-selector-item"
          :class="{ active: !modeVersionId || selectionStale }"
          @click="selectVersion(null)"
        >
          <Moon :size="14" />
          <span>{{ t('chat.followDefault') }}</span>
        </button>
        <template v-if="availableVersions.length">
          <div class="mode-selector-separator" />
          <div class="mode-selector-group">{{ t('chat.modeVersionGroup') }}</div>
          <p class="mode-selector-group-hint">{{ t('chat.modeVersionHint') }}</p>
          <button
            v-for="m in availableVersions"
            :key="m.id"
            class="mode-selector-item"
            :class="{ active: m.latest_published_mode_version_id === modeVersionId }"
            :title="versionSummary(m) || m.display_name"
            @click="selectVersion(m.latest_published_mode_version_id!)"
          >
            <component :is="modeIcon(m.slug)" :size="14" class="mode-selector-item-icon" />
            <span class="mode-selector-item-copy">
              <strong>{{ m.display_name }}</strong>
              <small v-if="versionSummary(m)">{{ versionSummary(m) }}</small>
            </span>
          </button>
        </template>
      </div>
    </Teleport>
  </div>
</template>
