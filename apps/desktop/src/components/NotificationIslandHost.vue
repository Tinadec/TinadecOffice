<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { AlertCircle, Bell, CheckCircle2, Info, TriangleAlert, X } from '@lucide/vue'
import { useNotifications, type NotificationItem, type NotificationLevel } from '@/composables/useNotifications'

const route = useRoute()
const { t } = useI18n()
const {
  items,
  primaryId,
  expandedId,
  orderedItems,
  visibleItems,
  primaryItem,
  overflowCount,
  dismiss,
  expand,
  collapse,
  promote,
  pause,
  resume,
  notify,
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

function heading(item: NotificationItem): string {
  if (item.title) return item.title
  if (item.level === 'success') return t('app.operationCompleted')
  if (item.level === 'error') return t('app.operationFailed')
  return item.level === 'warning' ? t('app.warning') : t('app.information')
}

function summary(item: NotificationItem): string {
  return item.title || item.message
}

function togglePrimary(item: NotificationItem): void {
  expandedId.value === item.id ? collapse() : expand(item.id)
}

function selectSide(item: NotificationItem): void {
  const wasExpanded = Boolean(expandedId.value)
  promote(item.id)
  if (wasExpanded) expand(item.id)
}

function selectQueueItem(item: NotificationItem): void {
  promote(item.id)
  expand(item.id)
}

function resumeUnlessExpanded(id: string, reason: string): void {
  if (expandedId.value !== id) resume(id, reason)
}

function handleFocusOut(event: FocusEvent, id: string): void {
  const current = event.currentTarget as HTMLElement
  if (!current.contains(event.relatedTarget as Node | null)) resumeUnlessExpanded(id, 'focus')
}

async function runAction(item: NotificationItem): Promise<void> {
  try {
    await item.action?.run()
  } catch (error) {
    notify.error(error)
  }
}

const announcedItem = ref<NotificationItem | null>(null)
const announcedIds = new Set<string>()
const queue = ref<HTMLElement | null>(null)
const peekId = ref<string | null>(null)
let peekTimer: ReturnType<typeof setTimeout> | null = null

watch(primaryId, (id) => {
  if (peekTimer) clearTimeout(peekTimer)
  peekId.value = id
  if (id) {
    peekTimer = setTimeout(() => {
      peekId.value = null
      peekTimer = null
    }, 2400)
  }
})

watch(() => items.value.map((item) => item.id), () => {
  const item = [...items.value].reverse().find((candidate) => !announcedIds.has(candidate.id))
  if (!item) return
  announcedIds.add(item.id)
  announcedItem.value = item
})

async function dismissQueueItem(item: NotificationItem, index: number): Promise<void> {
  dismiss(item.id)
  await nextTick()
  const controls = queue.value?.querySelectorAll<HTMLButtonElement>('.notification-card__queue-item > button:first-child')
  const target = controls?.[Math.min(index, Math.max(0, controls.length - 1))]
    ?? document.querySelector<HTMLButtonElement>('.notification-island--primary')
  target?.focus()
}

onBeforeUnmount(() => {
  if (peekTimer) clearTimeout(peekTimer)
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
      class="notification-host"
      :class="`notification-host--${routeKind}`"
      :aria-label="t('app.notificationRegion')"
    >
      <div class="notification-islands">
        <button
          v-for="(item, index) in visibleItems"
          :key="item.id"
          type="button"
          class="notification-island no-drag"
          :class="[
            `notification-island--${item.level}`,
            index === 0 ? 'notification-island--primary' : 'notification-island--side',
            index === 0 && peekId === item.id && expandedId !== item.id ? 'notification-island--peek' : '',
          ]"
          :aria-expanded="index === 0 ? expandedId === item.id : undefined"
          :title="index === 0 ? undefined : summary(item)"
          :aria-label="index === 0 && overflowCount ? `${summary(item)}, +${overflowCount} ${t('app.more')}` : summary(item)"
          @click="index === 0 ? togglePrimary(item) : selectSide(item)"
          @mouseenter="pause(item.id, 'hover')"
          @mouseleave="resumeUnlessExpanded(item.id, 'hover')"
          @focusin="pause(item.id, 'focus')"
          @focusout="handleFocusOut($event, item.id)"
        >
          <component :is="icon(item.level)" :size="14" aria-hidden="true" />
          <span class="notification-island__copy">
            <strong>{{ summary(item) }}</strong>
            <span v-if="index === 0 && item.title">{{ item.message }}</span>
          </span>
          <span
            v-if="index === 0 && overflowCount"
            class="notification-island__count"
            :title="t('app.more')"
          >+{{ overflowCount }}</span>
        </button>
      </div>

      <Transition name="notification-card">
        <article
          v-if="expandedId && primaryItem"
          class="notification-card no-drag"
          :class="`notification-card--${primaryItem.level}`"
          :role="primaryItem.level === 'error' ? 'alert' : 'status'"
          :aria-live="primaryItem.level === 'error' ? 'assertive' : 'polite'"
           @mouseenter="pause(primaryItem.id, 'hover')"
           @mouseleave="resumeUnlessExpanded(primaryItem.id, 'hover')"
           @focusin="pause(primaryItem.id, 'focus')"
          @focusout="handleFocusOut($event, primaryItem.id)"
        >
          <div class="notification-card__heading">
            <component :is="icon(primaryItem.level)" :size="17" aria-hidden="true" />
            <strong>{{ heading(primaryItem) }}</strong>
            <button
              type="button"
              class="notification-card__dismiss no-drag"
              :aria-label="t('app.dismiss')"
              :title="t('app.dismiss')"
              @click="dismiss(primaryItem.id)"
            >
              <X :size="15" aria-hidden="true" />
            </button>
          </div>
          <p>{{ primaryItem.message }}</p>
          <button
            v-if="primaryItem.action"
            type="button"
            class="notification-card__action no-drag"
            @click="runAction(primaryItem)"
          >
            {{ primaryItem.action.label }}
          </button>
          <div v-if="orderedItems.length > 1" ref="queue" class="notification-card__queue">
            <div v-for="(item, index) in orderedItems" :key="item.id" class="notification-card__queue-item">
              <button type="button" @click="selectQueueItem(item)">
                <component :is="icon(item.level)" :size="14" aria-hidden="true" />
                <span>{{ summary(item) }}</span>
              </button>
              <button
                type="button"
                class="notification-card__queue-dismiss"
                :aria-label="`${t('app.dismiss')}: ${summary(item)}`"
                @click="dismissQueueItem(item, index)"
              >
                <X :size="13" aria-hidden="true" />
              </button>
            </div>
          </div>
        </article>
      </Transition>
    </section>
  </Teleport>
</template>

<style scoped>
.notification-host {
  position: fixed;
  z-index: 10050;
  pointer-events: none;
  container-type: inline-size;
  display: flex;
  flex-direction: column;
  align-items: center;
  color: var(--text-primary, #18181b);
}

.notification-host--standard { top: 4px; left: 276px; right: 136px; }
.notification-host--detached { top: 44px; left: 8px; right: 8px; }
.notification-host--debug { top: 80px; left: 12px; right: 12px; }

.notification-islands {
  width: min(100%, 620px);
  height: 30px;
  display: flex;
  justify-content: center;
  gap: 5px;
}

.notification-island {
  --level: var(--accent-primary, #2563eb);
  height: 28px;
  min-width: 0;
  border: 1px solid color-mix(in srgb, var(--level) 30%, var(--border-default, #d4d4d8));
  border-radius: 999px;
  background: color-mix(in srgb, var(--level) 8%, var(--bg-primary, #fff));
  color: inherit;
  box-shadow: 0 4px 14px rgb(0 0 0 / 12%);
  pointer-events: auto;
  -webkit-app-region: no-drag;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 0 9px;
  font: inherit;
  font-size: 11px;
  cursor: pointer;
  overflow: hidden;
  transition: border-color 140ms ease, background-color 140ms ease, transform 140ms ease;
}

.notification-island:hover { transform: translateY(1px); }
.notification-island:focus-visible,
.notification-card button:focus-visible { outline: 2px solid var(--level, var(--accent-primary, #2563eb)); outline-offset: 2px; }
.notification-island--primary { flex: 0 1 220px; max-width: 220px; }
.notification-island--peek { flex-basis: 420px; max-width: 420px; }
.notification-island--side { flex: 0 1 112px; }
.notification-island--success, .notification-card--success { --level: var(--accent-success, #16845b); }
.notification-island--warning, .notification-card--warning { --level: var(--accent-warning, #b46900); }
.notification-island--error, .notification-card--error { --level: var(--accent-danger, #d13c4b); }

.notification-island__copy {
  min-width: 0;
  display: flex;
  gap: 5px;
  align-items: baseline;
  white-space: nowrap;
  overflow: hidden;
}

.notification-island__copy strong,
.notification-island__copy span { overflow: hidden; text-overflow: ellipsis; }
.notification-island__copy strong { flex: 0 1 auto; }
.notification-island__copy span { color: var(--text-secondary, #52525b); }
.notification-island__count { margin-left: auto; flex: none; color: var(--level); font-weight: 700; }

.notification-card {
  --level: var(--accent-primary, #2563eb);
  width: min(100%, 500px);
  margin-top: 6px;
  padding: 12px;
  border: 1px solid color-mix(in srgb, var(--level) 30%, var(--border-default, #d4d4d8));
  border-radius: 8px;
  background: color-mix(in srgb, var(--level) 5%, var(--bg-primary, #fff));
  box-shadow: 0 10px 30px rgb(0 0 0 / 18%);
  pointer-events: auto;
  -webkit-app-region: no-drag;
  overflow-wrap: anywhere;
}

.notification-card__heading { display: flex; align-items: center; gap: 8px; color: var(--level); }
.notification-card__heading strong { min-width: 0; color: var(--text-primary, #18181b); font-size: 13px; }
.notification-card p { margin: 7px 0 0 25px; color: var(--text-secondary, #52525b); font-size: 12px; line-height: 1.45; white-space: pre-wrap; }
.notification-card__dismiss {
  margin-left: auto;
  width: 24px;
  height: 24px;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--text-secondary, #52525b);
  display: grid;
  place-items: center;
  cursor: pointer;
}
.notification-card__dismiss:hover { background: color-mix(in srgb, var(--level) 12%, transparent); }
.notification-card__action {
  margin: 10px 0 0 25px;
  border: 1px solid color-mix(in srgb, var(--level) 45%, transparent);
  border-radius: 6px;
  background: color-mix(in srgb, var(--level) 12%, transparent);
  color: var(--text-primary, #18181b);
  padding: 5px 9px;
  font: inherit;
  font-size: 12px;
  cursor: pointer;
}
.notification-card__queue {
  max-height: min(240px, 35vh);
  margin-top: 10px;
  padding-top: 8px;
  border-top: 1px solid var(--border-muted, #e4e4e7);
  overflow-y: auto;
}
.notification-card__queue-item { display: flex; align-items: center; gap: 4px; }
.notification-card__queue-item > button:first-child {
  min-width: 0;
  flex: 1;
  display: flex;
  align-items: center;
  gap: 7px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-primary, #18181b);
  padding: 6px;
  text-align: left;
  cursor: pointer;
}
.notification-card__queue-item > button:first-child:hover { background: var(--bg-secondary, #f4f4f5); }
.notification-card__queue-item span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.notification-card__queue-dismiss {
  width: 26px;
  height: 26px;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--text-secondary, #52525b);
  display: grid;
  place-items: center;
  cursor: pointer;
}

.notification-card-enter-active,
.notification-card-leave-active { transition: opacity 140ms ease, transform 140ms ease; }
.notification-card-enter-from,
.notification-card-leave-to { opacity: 0; transform: translateY(-4px); }

@container (max-width: 460px) {
  .notification-island--side { flex-basis: 30px; padding: 0 7px; }
  .notification-island--side .notification-island__copy { display: none; }
  .notification-island--primary .notification-island__copy span { display: none; }
}

@container (max-width: 280px) {
  .notification-island--primary .notification-island__copy strong { display: none; }
}

@media (prefers-reduced-motion: reduce) {
  .notification-island,
  .notification-card-enter-active,
  .notification-card-leave-active { transition: none; }
}
</style>
