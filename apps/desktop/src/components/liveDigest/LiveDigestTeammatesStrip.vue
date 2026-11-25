<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { MessageSquarePlus, Pause, Users } from '@lucide/vue'
import type { LiveDigestTeammateThoughtDto } from '@/lib/liveDigestApi'
import TeammateSteerPopover, {
  type TeammateSteerAnchorRect,
} from '@/components/teammate/TeammateSteerPopover.vue'

const props = withDefaults(defineProps<{
  runId?: string | null
  teammates: readonly LiveDigestTeammateThoughtDto[]
  compact?: boolean
  enablePopover?: boolean
  storageId?: string
}>(), {
  runId: null,
  compact: false,
  enablePopover: true,
  storageId: undefined,
})

const emit = defineEmits<{
  'select-teammate': [payload: { teammate: LiveDigestTeammateThoughtDto; anchorRect: TeammateSteerAnchorRect | null }]
  'teammate-status-change': [payload: { handle: string; status: string; paused: boolean }]
}>()

const { t } = useI18n()

const popoverOpen = ref(false)
const activeHandle = ref<string | null>(null)
const anchorRect = ref<TeammateSteerAnchorRect | null>(null)
const localStatusOverrides = ref<Record<string, { status: string; paused: boolean }>>({})

const activeTeammate = computed<LiveDigestTeammateThoughtDto | null>(() => {
  if (!activeHandle.value) return null
  return props.teammates.find((item) => item.handle === activeHandle.value) ?? null
})

function effectiveStatus(item: LiveDigestTeammateThoughtDto): string {
  return localStatusOverrides.value[item.handle]?.status ?? item.status
}

function isPaused(item: LiveDigestTeammateThoughtDto): boolean {
  const override = localStatusOverrides.value[item.handle]
  if (override) return override.paused
  return String(item.status ?? '').toLowerCase() === 'paused'
}

function statusClass(status: string): string {
  const normalized = String(status ?? '').toLowerCase()
  if (normalized === 'working' || normalized === 'running') return 'is-working'
  if (normalized === 'paused') return 'is-paused'
  if (normalized === 'awaiting_approval') return 'is-approval'
  if (normalized === 'failed') return 'is-failed'
  if (normalized === 'completed') return 'is-completed'
  return 'is-waiting'
}

function statusLabel(status: string): string {
  const normalized = String(status ?? '').toLowerCase()
  if (normalized === 'working' || normalized === 'running') {
    return t('liveDigest.teammateStatus.working')
  }
  if (normalized === 'paused') return t('liveDigest.teammateStatus.paused')
  if (normalized === 'awaiting_approval') {
    return t('liveDigest.teammateStatus.awaitingApproval')
  }
  if (normalized === 'completed') return t('liveDigest.teammateStatus.completed')
  if (normalized === 'failed') return t('liveDigest.teammateStatus.failed')
  return t('liveDigest.teammateStatus.waiting')
}

function toAnchorRect(el: HTMLElement | null): TeammateSteerAnchorRect | null {
  if (!el || typeof el.getBoundingClientRect !== 'function') return null
  const r = el.getBoundingClientRect()
  return {
    top: r.top,
    left: r.left,
    right: r.right,
    bottom: r.bottom,
    width: r.width,
    height: r.height,
  }
}

function onSelectTeammate(item: LiveDigestTeammateThoughtDto, event: MouseEvent): void {
  const rect = toAnchorRect(event.currentTarget as HTMLElement | null)
  emit('select-teammate', { teammate: item, anchorRect: rect })
  if (!props.enablePopover) return
  if (popoverOpen.value && activeHandle.value === item.handle) {
    popoverOpen.value = false
    activeHandle.value = null
    return
  }
  activeHandle.value = item.handle
  anchorRect.value = rect
  popoverOpen.value = true
}

function onPopoverStatusChange(payload: { handle: string; status: string; paused: boolean }): void {
  localStatusOverrides.value = {
    ...localStatusOverrides.value,
    [payload.handle]: { status: payload.status, paused: payload.paused },
  }
  emit('teammate-status-change', payload)
}

function onPopoverClose(): void {
  popoverOpen.value = false
}
</script>

<template>
  <section
    v-if="teammates.length > 0"
    class="ld-teammates"
    :class="{ 'is-compact': compact }"
    :aria-label="t('liveDigest.teammates.sectionLabel')"
    data-testid="live-digest-teammates-strip"
  >
    <div class="ld-teammates-head">
      <Users :size="13" class="ld-teammates-head-icon" aria-hidden="true" />
      <span class="ld-teammates-head-title">
        {{ t('liveDigest.teammates.title', { count: teammates.length }) }}
      </span>
      <span class="ld-teammates-hint">
        {{ t('liveDigest.teammates.clickToSteer') }}
      </span>
    </div>

    <div class="ld-teammates-row" role="list">
      <button
        v-for="item in teammates"
        :key="item.handle"
        type="button"
        role="listitem"
        class="ld-teammate-chip ui-pressable"
        :class="[
          statusClass(effectiveStatus(item)),
          { 'is-selected': popoverOpen && activeHandle === item.handle },
        ]"
        :data-teammate-handle="item.handle"
        :title="t('teammateControl.triggerTooltip', { name: item.display_name || item.handle })"
        data-testid="live-digest-teammate-card"
        @click="onSelectTeammate(item, $event)"
      >
        <span class="ld-teammate-top">
          <span class="ld-teammate-dot" :class="statusClass(effectiveStatus(item))" aria-hidden="true" />
          <span class="ld-teammate-name">{{ item.display_name || item.handle }}</span>
          <span class="ld-teammate-status">
            <Pause v-if="isPaused(item)" :size="10" aria-hidden="true" />
            <span>{{ statusLabel(effectiveStatus(item)) }}</span>
          </span>
          <MessageSquarePlus :size="12" class="ld-teammate-steer-icon" aria-hidden="true" />
        </span>
        <span class="ld-teammate-thought">
          {{ item.thought || item.current_task || t('liveDigest.teammates.idleThought') }}
        </span>
      </button>
    </div>

    <TeammateSteerPopover
      v-if="enablePopover && activeTeammate"
      v-model:open="popoverOpen"
      :run-id="runId"
      :handle="activeTeammate.handle"
      :display-name="activeTeammate.display_name || activeTeammate.handle"
      :status="effectiveStatus(activeTeammate)"
      :paused="isPaused(activeTeammate)"
      :task-summary="activeTeammate.current_task ?? null"
      :thought="activeTeammate.thought"
      :anchor-rect="anchorRect"
      :storage-id="storageId"
      @status-change="onPopoverStatusChange"
      @close="onPopoverClose"
    />
  </section>
</template>

<style scoped>
.ld-teammates {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
}

.ld-teammates-head {
  display: flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
}

.ld-teammates-head-icon {
  color: var(--text-muted);
  flex-shrink: 0;
}

.ld-teammates-head-title {
  font-size: 11.5px;
  font-weight: 600;
  color: var(--text-secondary);
}

.ld-teammates-hint {
  margin-left: auto;
  font-size: 11px;
  color: var(--text-muted);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.ld-teammates-row {
  display: flex;
  align-items: stretch;
  gap: 7px;
  overflow-x: auto;
  padding-bottom: 2px;
  min-width: 0;
}

.ld-teammate-chip {
  display: flex;
  flex-direction: column;
  gap: 4px;
  flex: 0 0 auto;
  min-width: 152px;
  max-width: 224px;
  padding: 7px 9px;
  border-radius: var(--radius-control-md, 8px);
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-primary);
  text-align: left;
  cursor: pointer;
}

.ld-teammate-chip:hover,
.ld-teammate-chip.is-selected {
  background: var(--surface-hover, var(--bg-hover));
  border-color: var(--border-default);
}

.ld-teammate-chip.is-paused {
  border-color: color-mix(in srgb, var(--accent-warning) 42%, var(--border-muted));
}

.ld-teammate-chip.is-approval {
  border-color: color-mix(in srgb, var(--accent-warning) 55%, var(--border-muted));
}

.ld-teammate-chip.is-failed {
  border-color: color-mix(in srgb, var(--accent-danger) 48%, var(--border-muted));
}

.ld-teammate-top {
  display: flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
}

.ld-teammate-dot {
  width: 6px;
  height: 6px;
  border-radius: 999px;
  background: var(--text-muted);
  flex-shrink: 0;
}

.ld-teammate-dot.is-working {
  background: var(--accent-success);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent-success) 20%, transparent);
}

.ld-teammate-dot.is-paused,
.ld-teammate-dot.is-approval {
  background: var(--accent-warning);
}

.ld-teammate-dot.is-failed {
  background: var(--accent-danger);
}

.ld-teammate-dot.is-completed {
  background: var(--text-secondary);
}

.ld-teammate-name {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex: 1;
  min-width: 0;
}

.ld-teammate-status {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  font-size: 10.5px;
  color: var(--text-muted);
  flex-shrink: 0;
}

.ld-teammate-steer-icon {
  color: var(--text-muted);
  flex-shrink: 0;
  opacity: 0.65;
  transition: opacity var(--duration-tap, 120ms) var(--ease-snappy, ease);
}

.ld-teammate-chip:hover .ld-teammate-steer-icon,
.ld-teammate-chip.is-selected .ld-teammate-steer-icon {
  opacity: 1;
  color: var(--text-primary);
}

.ld-teammate-thought {
  font-size: 11.5px;
  line-height: 1.35;
  color: var(--text-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ld-teammates.is-compact .ld-teammate-chip {
  min-width: 136px;
  max-width: 196px;
  padding: 6px 8px;
}
</style>
