<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { AlertCircle, Bell, CheckCircle2, Info, TriangleAlert } from '@lucide/vue'
import {
  useNotifications,
  type NotificationItem,
  type NotificationLevel,
} from '@/composables/useNotifications'

const route = useRoute()
const { t } = useI18n()
const {
  items,
  primaryId,
  detailId,
  hoveredId,
  visibleItems,
  overflowCount,
  openDetail,
  setHovered,
  promote,
} = useNotifications()

const routeKind = computed(() => {
  if (route.name === 'detached-panel') return 'detached'
  if (route.name === 'debug-studio') return 'debug'
  if (route.name === 'desktop-pet') return 'pet'
  return 'standard'
})

const icons = {
  info: Info,
  success: CheckCircle2,
  warning: TriangleAlert,
  error: AlertCircle,
}

function icon(level: NotificationLevel) {
  return icons[level] ?? Bell
}

function summary(item: NotificationItem): string {
  return item.title || item.message
}

function capsuleLabel(item: NotificationItem, index: number): string {
  const base = summary(item)
  if (index === 0 && overflowCount.value) {
    return `${base}, +${overflowCount.value} ${t('app.more')}`
  }
  return base
}

/** Compact label for side islands — icon + short text */
function shortLabel(item: NotificationItem): string {
  if (item.title) return item.title
  const msg = item.message
  return msg.length > 18 ? `${msg.slice(0, 16)}…` : msg
}

const localHoverId = ref<string | null>(null)
let hoverLeaveTimer: ReturnType<typeof setTimeout> | null = null

function onEnter(item: NotificationItem): void {
  if (hoverLeaveTimer) {
    clearTimeout(hoverLeaveTimer)
    hoverLeaveTimer = null
  }
  localHoverId.value = item.id
  setHovered(item.id)
  if (item.id !== primaryId.value) promote(item.id)
}

function onLeave(item: NotificationItem): void {
  if (hoverLeaveTimer) clearTimeout(hoverLeaveTimer)
  hoverLeaveTimer = setTimeout(() => {
    if (localHoverId.value === item.id) {
      localHoverId.value = null
      setHovered(null)
    }
    hoverLeaveTimer = null
  }, 80)
}

function onClick(item: NotificationItem): void {
  openDetail(item.id)
}

function isPeek(item: NotificationItem, index: number): boolean {
  if (index !== 0) return false
  if (detailId.value === item.id) return false
  return hoveredId.value === item.id || localHoverId.value === item.id
}

const announcedItem = ref<NotificationItem | null>(null)
const announcedIds = new Set<string>()

watch(
  () => items.value.map((item) => item.id),
  () => {
    const item = [...items.value].reverse().find((candidate) => !announcedIds.has(candidate.id))
    if (!item) return
    announcedIds.add(item.id)
    announcedItem.value = item
  },
)

onBeforeUnmount(() => {
  if (hoverLeaveTimer) clearTimeout(hoverLeaveTimer)
  setHovered(null)
})
</script>

<template>
  <Teleport to="body">
    <div class="sr-only" aria-live="polite" aria-atomic="true">
      {{ announcedItem?.level !== 'error' ? announcedItem?.message : '' }}
    </div>
    <div class="sr-only" aria-live="assertive" aria-atomic="true">
      {{ announcedItem?.level === 'error' ? announcedItem.message : '' }}
    </div>

    <section
      v-if="items.length && routeKind !== 'pet'"
      class="island-host"
      :class="`island-host--${routeKind}`"
      :aria-label="t('app.notificationRegion')"
    >
      <div class="island-row">
        <button
          v-for="(item, index) in visibleItems"
          :key="item.id"
          type="button"
          class="island-capsule no-drag"
          :class="[
            `island-capsule--${item.level}`,
            index === 0 ? 'island-capsule--primary' : 'island-capsule--side',
            isPeek(item, index) ? 'island-capsule--peek' : '',
            detailId === item.id ? 'island-capsule--active' : '',
          ]"
          :aria-label="capsuleLabel(item, index)"
          :title="summary(item)"
          @mouseenter="onEnter(item)"
          @mouseleave="onLeave(item)"
          @focusin="onEnter(item)"
          @focusout="onLeave(item)"
          @click="onClick(item)"
        >
          <component :is="icon(item.level)" :size="14" class="island-capsule__icon" aria-hidden="true" />
          <span class="island-capsule__text">
            <template v-if="index === 0">
              <strong class="island-capsule__title">{{ item.title || shortLabel(item) }}</strong>
              <span v-if="isPeek(item, index) && item.title" class="island-capsule__msg">{{ item.message }}</span>
              <span v-else-if="isPeek(item, index) && !item.title" class="island-capsule__msg">{{ item.message }}</span>
            </template>
            <template v-else>
              <strong class="island-capsule__title">{{ shortLabel(item) }}</strong>
            </template>
          </span>
          <span
            v-if="index === 0 && overflowCount"
            class="island-capsule__badge"
          >+{{ overflowCount }}</span>
        </button>
      </div>
    </section>
  </Teleport>
</template>

<style scoped>
.island-host {
  position: fixed;
  z-index: 10050;
  pointer-events: none;
  display: flex;
  justify-content: center;
  container-type: inline-size;
}

.island-host--standard {
  top: 6px;
  left: 200px;
  right: 140px;
}

.island-host--detached {
  top: 42px;
  left: 8px;
  right: 8px;
}

.island-host--debug {
  top: 78px;
  left: 12px;
  right: 12px;
}

.island-row {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  max-width: 100%;
  pointer-events: none;
}

.island-capsule {
  --level: var(--accent-primary, #2ec4b6);
  pointer-events: auto;
  -webkit-app-region: no-drag;
  display: flex;
  align-items: center;
  gap: 7px;
  height: 28px;
  min-width: 0;
  max-width: 160px;
  padding: 0 12px;
  border: 1px solid color-mix(in srgb, var(--level) 35%, var(--border-default, #1a1f29));
  border-radius: 999px;
  background: color-mix(in srgb, var(--bg-secondary, #11151c) 88%, var(--level));
  color: var(--text-primary, #c9d1d9);
  box-shadow:
    0 1px 2px rgb(0 0 0 / 18%),
    0 6px 18px rgb(0 0 0 / 22%);
  font: inherit;
  font-size: 11px;
  line-height: 1;
  cursor: pointer;
  overflow: hidden;
  transition:
    max-width 200ms cubic-bezier(0.2, 0.8, 0.2, 1),
    padding 200ms cubic-bezier(0.2, 0.8, 0.2, 1),
    background-color 160ms ease,
    border-color 160ms ease,
    transform 160ms ease,
    box-shadow 160ms ease;
}

.island-capsule--primary {
  max-width: 200px;
  flex: 0 1 auto;
}

.island-capsule--side {
  max-width: 96px;
  padding: 0 10px;
  opacity: 0.92;
}

.island-capsule--peek {
  max-width: min(420px, 100cqw);
  padding: 0 14px;
  transform: translateY(1px);
  box-shadow:
    0 2px 4px rgb(0 0 0 / 20%),
    0 10px 28px rgb(0 0 0 / 28%);
  background: color-mix(in srgb, var(--bg-secondary, #11151c) 78%, var(--level));
}

.island-capsule--active {
  border-color: color-mix(in srgb, var(--level) 55%, var(--border-default, #1a1f29));
}

.island-capsule--success { --level: var(--accent-success, #2ec4b6); }
.island-capsule--warning { --level: var(--accent-warning, #d29922); }
.island-capsule--error { --level: var(--accent-danger, #f85149); }
.island-capsule--info { --level: var(--accent-primary, #2ec4b6); }

.island-capsule__icon {
  flex: none;
  color: var(--level);
}

.island-capsule__text {
  min-width: 0;
  display: flex;
  align-items: baseline;
  gap: 6px;
  overflow: hidden;
  white-space: nowrap;
}

.island-capsule__title {
  overflow: hidden;
  text-overflow: ellipsis;
  font-weight: 600;
}

.island-capsule__msg {
  overflow: hidden;
  text-overflow: ellipsis;
  color: var(--text-secondary, #7d8590);
  font-weight: 400;
}

.island-capsule__badge {
  flex: none;
  margin-left: 2px;
  font-weight: 700;
  color: var(--level);
  font-size: 10px;
}

.island-capsule:focus-visible {
  outline: 2px solid var(--level);
  outline-offset: 2px;
}

@container (max-width: 420px) {
  .island-capsule--side .island-capsule__text {
    display: none;
  }
  .island-capsule--side {
    max-width: 32px;
    padding: 0 9px;
  }
  .island-capsule--primary {
    max-width: min(280px, 100cqw);
  }
}

@media (prefers-reduced-motion: reduce) {
  .island-capsule {
    transition: none;
  }
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}
</style>
