<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import {
  AlertCircle,
  Bell,
  CheckCircle2,
  FileText,
  Info,
  LoaderCircle,
  RotateCw,
  TriangleAlert,
  X,
} from '@lucide/vue'
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
  pinnedId,
  visibleItems,
  expandedItem,
  overflowCount,
  openDetail,
  setHovered,
  togglePinned,
  closeExpanded,
  runAction,
  actionStates,
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

const host = ref<HTMLElement | null>(null)

function onEnter(item: NotificationItem): void {
  setHovered(item.id)
}

function onLeave(): void {
  setHovered(null)
}

function onClick(item: NotificationItem): void {
  togglePinned(item.id)
}

function closeCard(): void {
  setHovered(null)
  closeExpanded()
}

function onOutsideClick(event: MouseEvent): void {
  if (pinnedId.value && !host.value?.contains(event.target as Node)) closeCard()
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && pinnedId.value) closeCard()
}

function onFocusOut(event: FocusEvent): void {
  if (!host.value?.contains(event.relatedTarget as Node | null)) setHovered(null)
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

onMounted(() => {
  document.addEventListener('mousedown', onOutsideClick)
  document.addEventListener('keydown', onKeydown)
})

onBeforeUnmount(() => {
  document.removeEventListener('mousedown', onOutsideClick)
  document.removeEventListener('keydown', onKeydown)
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
      ref="host"
      class="island-host"
      :class="`island-host--${routeKind}`"
      :aria-label="t('app.notificationRegion')"
      @mouseleave="onLeave"
      @focusout="onFocusOut"
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
            expandedItem?.id === item.id ? 'island-capsule--active' : '',
          ]"
          :aria-label="capsuleLabel(item, index)"
          :data-notification-id="item.id"
          :aria-expanded="expandedItem?.id === item.id"
          :aria-controls="expandedItem?.id === item.id ? 'notification-island-card' : undefined"
          :title="summary(item)"
          @mouseenter="onEnter(item)"
          @focusin="onEnter(item)"
          @click="onClick(item)"
        >
          <component :is="icon(item.level)" :size="14" class="island-capsule__icon" aria-hidden="true" />
          <span class="island-capsule__text">
            <template v-if="index === 0">
              <strong class="island-capsule__title">{{ item.title || shortLabel(item) }}</strong>
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

      <Transition name="island-card">
        <article
          v-if="expandedItem"
          id="notification-island-card"
          class="island-card no-drag"
          :class="`island-card--${expandedItem.level}`"
        >
          <header class="island-card__header">
            <span class="island-card__icon" aria-hidden="true">
              <component :is="icon(expandedItem.level)" :size="17" />
            </span>
            <div class="island-card__heading">
              <strong>{{ expandedItem.title || summary(expandedItem) }}</strong>
              <span>{{ expandedItem.kind === 'banner' ? t('app.systemStatus') : t('app.notification') }}</span>
            </div>
            <button
              type="button"
              class="island-card__icon-button"
              :aria-label="t('app.close')"
              @click.stop="closeCard"
            >
              <X :size="15" aria-hidden="true" />
            </button>
          </header>

          <p class="island-card__summary">{{ expandedItem.message }}</p>
          <p v-if="actionStates[expandedItem.id]?.error" class="island-card__error" role="alert">
            <AlertCircle :size="14" aria-hidden="true" />
            {{ actionStates[expandedItem.id]?.error }}
          </p>

          <footer class="island-card__actions">
            <button type="button" class="island-card__secondary" @click="openDetail(expandedItem.id)">
              <FileText :size="14" aria-hidden="true" />
              {{ t('app.viewDetails') }}
            </button>
            <button
              v-if="expandedItem.action"
              type="button"
              class="island-card__primary"
              :disabled="actionStates[expandedItem.id]?.running"
              @click="runAction(expandedItem.id)"
            >
              <LoaderCircle
                v-if="actionStates[expandedItem.id]?.running"
                :size="14"
                class="island-card__spinner"
                aria-hidden="true"
              />
              <RotateCw v-else :size="14" aria-hidden="true" />
              {{ expandedItem.action.label }}
            </button>
          </footer>
        </article>
      </Transition>
    </section>
  </Teleport>
</template>

<style scoped>
.island-host {
  position: fixed;
  z-index: 10050;
  pointer-events: none;
  display: flex;
  flex-direction: column;
  align-items: center;
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

.island-capsule--active {
  border-color: color-mix(in srgb, var(--level) 55%, var(--border-default, #1a1f29));
  background: color-mix(in srgb, var(--bg-secondary, #11151c) 72%, var(--level));
  box-shadow: 0 6px 20px rgb(0 0 0 / 28%);
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

.island-card {
  --level: var(--accent-primary, #2ec4b6);
  pointer-events: auto;
  width: min(340px, 100cqw);
  margin-top: 8px;
  padding: 13px;
  border: 1px solid color-mix(in srgb, var(--level) 32%, var(--border-default, #1a1f29));
  border-radius: 8px;
  background: color-mix(in srgb, var(--bg-primary, #0a0e14) 86%, transparent);
  color: var(--text-primary, #c9d1d9);
  box-shadow: 0 16px 44px rgb(0 0 0 / 42%);
  backdrop-filter: blur(18px) saturate(125%);
  -webkit-backdrop-filter: blur(18px) saturate(125%);
  -webkit-app-region: no-drag;
}

.island-card--success { --level: var(--accent-success, #2ec4b6); }
.island-card--warning { --level: var(--accent-warning, #d29922); }
.island-card--error { --level: var(--accent-danger, #f85149); }
.island-card--info { --level: var(--accent-primary, #2ec4b6); }

.island-card__header {
  display: flex;
  align-items: center;
  gap: 9px;
}

.island-card__icon {
  display: grid;
  place-items: center;
  width: 30px;
  height: 30px;
  flex: none;
  border-radius: 7px;
  background: color-mix(in srgb, var(--level) 14%, transparent);
  color: var(--level);
}

.island-card__heading {
  display: grid;
  min-width: 0;
  flex: 1;
  gap: 3px;
}

.island-card__heading strong,
.island-card__heading span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.island-card__heading strong {
  font-size: 12px;
  line-height: 1.25;
}

.island-card__heading span {
  color: var(--text-tertiary, #656d76);
  font-size: 10px;
}

.island-card__icon-button {
  display: grid;
  place-items: center;
  width: 27px;
  height: 27px;
  flex: none;
  padding: 0;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary, #7d8590);
  cursor: pointer;
}

.island-card__icon-button:hover,
.island-card__secondary:hover {
  background: var(--bg-hover, #161b22);
  color: var(--text-primary, #c9d1d9);
}

.island-card__summary {
  margin: 11px 0 0;
  color: var(--text-secondary, #7d8590);
  font-size: 12px;
  line-height: 1.45;
  overflow-wrap: anywhere;
}

.island-card__error {
  display: flex;
  align-items: flex-start;
  gap: 7px;
  margin: 9px 0 0;
  padding: 8px;
  border-radius: 6px;
  background: color-mix(in srgb, var(--accent-danger, #f85149) 10%, transparent);
  color: var(--accent-danger, #f85149);
  font-size: 11px;
  line-height: 1.4;
}

.island-card__error svg {
  flex: none;
  margin-top: 1px;
}

.island-card__actions {
  display: flex;
  justify-content: flex-end;
  gap: 7px;
  margin-top: 12px;
}

.island-card__actions button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  min-height: 29px;
  padding: 5px 10px;
  border: 1px solid var(--border-default, #1a1f29);
  border-radius: 6px;
  font: inherit;
  font-size: 11px;
  cursor: pointer;
}

.island-card__secondary {
  background: transparent;
  color: var(--text-secondary, #7d8590);
}

.island-card__primary {
  border-color: color-mix(in srgb, var(--level) 55%, transparent) !important;
  background: color-mix(in srgb, var(--level) 18%, var(--bg-secondary, #11151c));
  color: var(--level);
  font-weight: 600;
}

.island-card__actions button:disabled {
  opacity: 0.55;
  cursor: wait;
}

.island-card__spinner {
  animation: island-spin 800ms linear infinite;
}

.island-card-enter-active,
.island-card-leave-active {
  transition: opacity 140ms ease, transform 160ms ease;
  transform-origin: top center;
}

.island-card-enter-from,
.island-card-leave-to {
  opacity: 0;
  transform: translateY(-5px) scale(0.98);
}

@keyframes island-spin {
  to { transform: rotate(360deg); }
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
  .island-capsule,
  .island-card-enter-active,
  .island-card-leave-active {
    transition: none;
  }
  .island-card__spinner { animation: none; }
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
