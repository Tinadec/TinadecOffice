<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  Play,
  Network,
  UserCheck,
  ShieldCheck,
  Package,
  CheckCircle2,
  AlertCircle,
  MessageSquare,
  Terminal,
  ChevronDown,
  ChevronUp,
  Activity,
} from '@lucide/vue'
import type { ProgressEvent } from '@/composables/useAgentActivity'

const props = defineProps<{
  events: ProgressEvent[]
}>()

const showAll = ref(false)

const iconMap: Record<string, unknown> = {
  play: Play,
  network: Network,
  'user-check': UserCheck,
  shield: ShieldCheck,
  package: Package,
  'check-circle': CheckCircle2,
  'alert-circle': AlertCircle,
  'message-square': MessageSquare,
  terminal: Terminal,
  check: CheckCircle2,
  x: AlertCircle,
}

const visibleEvents = computed(() => {
  if (showAll.value) return [...props.events].reverse()
  return [...props.events].slice(-5).reverse()
})

const hiddenCount = computed(() => Math.max(0, props.events.length - 5))

const hasEvents = computed(() => props.events.length > 0)

function formatTime(ts: string): string {
  try {
    return new Date(ts).toLocaleTimeString('zh-CN', {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    })
  } catch {
    return ''
  }
}

function getIcon(iconKey: string) {
  return iconMap[iconKey] ?? Activity
}
</script>

<template>
  <section v-if="hasEvents" class="progress-stream">
    <div class="progress-stream-header">
      <span class="progress-stream-title">实时活动</span>
      <button
        v-if="hiddenCount > 0"
        class="progress-stream-toggle"
        type="button"
        @click="showAll = !showAll"
      >
        {{ showAll ? '收起' : `还有 ${hiddenCount} 条` }}
        <component :is="showAll ? ChevronUp : ChevronDown" :size="11" />
      </button>
    </div>

    <div class="progress-stream-list">
      <TransitionGroup name="progress-slide">
        <div
          v-for="event in visibleEvents"
          :key="event.id"
          class="progress-stream-item"
        >
          <div class="progress-stream-item-icon">
            <component :is="getIcon(event.icon)" :size="11" />
          </div>
          <div class="progress-stream-item-body">
            <span class="progress-stream-item-text">{{ event.message }}</span>
            <span class="progress-stream-item-time">{{ formatTime(event.timestamp) }}</span>
          </div>
        </div>
      </TransitionGroup>
    </div>
  </section>
</template>

<style scoped>
.progress-stream {
  border-top: 1px solid var(--border-muted);
  background: var(--bg-secondary);
  padding: 8px 12px;
  max-height: 180px;
  overflow: hidden;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.progress-stream-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.progress-stream-title {
  font-size: 11px;
  font-weight: 700;
  color: var(--text-muted);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.progress-stream-toggle {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 2px 6px;
  border: none;
  border-radius: 4px;
  font-size: 10px;
  color: var(--text-muted);
  background: transparent;
  cursor: pointer;
  transition: color 0.15s, background 0.15s;
}

.progress-stream-toggle:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.progress-stream-list {
  display: flex;
  flex-direction: column;
  gap: 3px;
  overflow-y: auto;
  min-height: 0;
}

.progress-stream-item {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  padding: 3px 0;
}

.progress-stream-item-icon {
  display: grid;
  place-items: center;
  width: 16px;
  height: 16px;
  flex-shrink: 0;
  border-radius: 4px;
  background: var(--bg-tertiary);
  color: var(--text-secondary);
  margin-top: 1px;
}

.progress-stream-item-body {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  flex: 1;
}

.progress-stream-item-text {
  overflow: hidden;
  font-size: 11px;
  color: var(--text-secondary);
  text-overflow: ellipsis;
  white-space: nowrap;
  flex: 1;
  min-width: 0;
}

.progress-stream-item-time {
  flex-shrink: 0;
  font-size: 10px;
  color: var(--text-muted);
}

.progress-slide-enter-active {
  transition: all 0.3s ease;
}

.progress-slide-leave-active {
  transition: all 0.2s ease;
  position: absolute;
}

.progress-slide-enter-from {
  opacity: 0;
  transform: translateX(-12px);
}

.progress-slide-leave-to {
  opacity: 0;
  transform: translateX(12px);
}
</style>
