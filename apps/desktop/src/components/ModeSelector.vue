<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick } from 'vue'
import { ChevronDown, FileSearch, HelpCircle, Map, Sparkles, Zap, Network } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { api, type AgentModeTopologyDto } from '@/api'
import type { AgentMode } from '@/types/mode'

const { t } = useI18n()
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
const panelStyle = computed(() => getPanelStyle())
const panelDataAttrs = computed(() => getPanelDataAttributes())

interface ModeOption {
  key: AgentMode
  label: string
  icon: any
}

/** agent_mode 契约固定为这六个词（Core `InteractionsEndpoints`）；图标本地维护。 */
const MODE_ICONS: Record<AgentMode, any> = {
  plan: Map,
  spec: FileSearch,
  ask: HelpCircle,
  vibe: Sparkles,
  auto: Zap,
  agent: Network,
}
const MODE_ORDER: AgentMode[] = ['plan', 'spec', 'ask', 'vibe', 'auto', 'agent']

// 显示名优先取 pack 安装的 conversation.* 模式（`问答 (Ask)` 这类中文名），
// 网关离线时回退到 i18n 词表。这两者指向同一个 agent_mode，不能各显示一遍。
const modes = computed<ModeOption[]>(() =>
  MODE_ORDER.map((key) => ({
    key,
    label: conversationModes.value.find((m) => m.application_mode === key)?.display_name ?? t(`mode.${key}`),
    icon: MODE_ICONS[key],
  }))
)

const props = defineProps<{
  modelValue: AgentMode
  /** Explicit published ModeVersion override; null = follow the agent-mode default. */
  modeVersionId?: string | null
}>()

const emit = defineEmits<{
  'update:modelValue': [value: AgentMode]
  'update:modeVersionId': [value: string | null]
}>()

const showDropdown = ref(false)
const triggerRef = ref<HTMLElement | null>(null)
const dropdownStyle = ref<Record<string, string>>({})

const currentMode = computed(() => modes.value.find(m => m.key === props.modelValue) ?? modes.value[0])

// 模式选择器（配置体验改造 B）：
//  - 「跟随默认」= 不传 mode_version_id，会话沿用其绑定或工作区默认；
//  - 「对话模式」= agent_mode 词表（提交 agent_mode，同时清掉 mode_version_id）；
//  - 「模式拓扑」= 工作区已发布模式（提交 latest_published_mode_version_id）。
// draft/archived/同 slug 重复项由服务端过滤，这里永不出现；已选值失效时自愈回「跟随默认」。
const modeVersions = ref<AgentModeTopologyDto[]>([])
/** conversation.* 模式：application_mode 非空，已经由上面的对话模式组承载。 */
const conversationModes = computed(() => modeVersions.value.filter(v => !!v.application_mode))
// 拓扑组只列工作区自建拓扑（application_mode == null）。带 application_mode 的
// pack 模式会被对话模式组显示，留在这里就是同一批东西显示两遍。
const publishedVersions = computed(() =>
  modeVersions.value.filter(v => v.status === 'published' && !!v.latest_published_mode_version_id && !v.application_mode)
)
const selectedVersion = computed(() =>
  publishedVersions.value.find(v => v.latest_published_mode_version_id === props.modeVersionId) ?? null
)
// 已选 version 失效（被下架/跨工作区残留）时不再高亮任何拓扑项——由 trigger
// 显示「跟随默认」，发送时 mode_version_id 会被清空，避免命中 409 mode_unavailable。
const selectionStale = computed(() => !!props.modeVersionId && !selectedVersion.value)
const triggerLabel = computed(() => selectedVersion.value?.display_name ?? currentMode.value.label)

async function loadModeVersions() {
  try {
    const list = await api.listAgentModeTopologies()
    modeVersions.value = Array.isArray(list) ? (list as AgentModeTopologyDto[]) : []
  } catch { /* gateway offline：回退 agent_mode 词表 */ }
}

function selectVersion(versionId: string | null) {
  if (versionId !== null && !publishedVersions.value.some(v => v.latest_published_mode_version_id === versionId)) {
    // OpenCode 式防呆：列表里不存在的项根本设置不进去。
    return
  }
  emit('update:modeVersionId', versionId)
  showDropdown.value = false
}

function selectMode(key: AgentMode) {
  // 选回对话模式必须同时清掉 mode_version_id，否则旧 version 优先于 agent_mode 生效。
  emit('update:modelValue', key)
  emit('update:modeVersionId', null)
  showDropdown.value = false
}

function updateDropdownPosition() {
  const trigger = triggerRef.value
  if (!trigger) return
  const rect = trigger.getBoundingClientRect()
  const vh = window.innerHeight
  const vw = window.innerWidth
  const ddWidth = 180
  const estH = 220
  const spaceBelow = vh - rect.bottom
  const spaceAbove = rect.top
  const flip = spaceBelow < estH && spaceAbove > spaceBelow
  const left = Math.max(8, Math.min(rect.left, vw - ddWidth - 8))
  if (flip) {
    dropdownStyle.value = {
      position: 'fixed',
      bottom: `${vh - rect.top + 6}px`,
      left: `${left}px`,
      minWidth: '180px',
      maxHeight: `${Math.min(280, spaceAbove - 12)}px`,
      overflowY: 'auto',
    }
  } else {
    dropdownStyle.value = {
      position: 'fixed',
      top: `${rect.bottom + 6}px`,
      left: `${left}px`,
      minWidth: '180px',
      maxHeight: `${Math.min(280, spaceBelow - 12)}px`,
      overflowY: 'auto',
    }
  }
}

async function toggleDropdown() {
  showDropdown.value = !showDropdown.value
  if (showDropdown.value) {
    // 每次打开都刷新：智能体中心发布新版本 / 安装 pack 后立即可见。
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
      <component :is="selectedVersion ? Sparkles : currentMode.icon" :size="14" />
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
          <component :is="currentMode.icon" :size="14" />
          <span>{{ t('chat.followDefault') }}</span>
        </button>
        <div class="mode-selector-separator" />
        <div class="mode-selector-group">{{ t('chat.conversationModeGroup') }}</div>
        <button
          v-for="mode in modes"
          :key="mode.key"
          class="mode-selector-item"
          :class="{ active: (!modeVersionId || selectionStale) && mode.key === modelValue }"
          @click="selectMode(mode.key)"
        >
          <component :is="mode.icon" :size="14" />
          <span>{{ mode.label }}</span>
        </button>
        <template v-if="publishedVersions.length">
          <div class="mode-selector-separator" />
          <div class="mode-selector-group">{{ t('chat.modeTopologyGroup') }}</div>
          <button
            v-for="m in publishedVersions"
            :key="m.id"
            class="mode-selector-item"
            :class="{ active: m.latest_published_mode_version_id === modeVersionId }"
            @click="selectVersion(m.latest_published_mode_version_id!)"
          >
            <Sparkles :size="14" />
            <span>{{ m.display_name }}</span>
          </button>
        </template>
      </div>
    </Teleport>
  </div>
</template>
