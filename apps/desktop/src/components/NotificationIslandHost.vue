<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import {
  AlertCircle, Bell, CheckCircle2, FileText, Info,
  LoaderCircle, MoreHorizontal, Pin, RotateCw, TriangleAlert, X,
} from '@lucide/vue'
import {
  isUserDismissible,
  useNotifications,
  type NotificationItem,
  type NotificationLevel,
} from '@/composables/useNotifications'

const route = useRoute()
const { t, locale } = useI18n()
const {
  items, history, statusZone, transientZone, orderedItems,
  overflowCount, overflowOpen, openItem,
  actionStates,
  closeOpen, toggleOverflow, closeOverflow,
  openDetail, dismiss, dismissAll, clearHistory, runAction,
  hoverCapsuleEnter, hoverCapsuleLeave, hoverCardEnter, hoverCardLeave,
  openNotification,
} = useNotifications()

/** Live items the user is allowed to close — drives the "dismiss all" button. */
const dismissibleLiveCount = computed(
  () => orderedItems.value.filter((item) => isUserDismissible(item)).length,
)

const routeKind = computed(() => {
  if (route.name === 'detached-panel') return 'detached'
  if (route.name === 'debug-studio') return 'debug'
  if (route.name === 'desktop-pet') return 'pet'
  return 'standard'
})

const icons: Record<NotificationLevel, typeof Bell> = {
  info: Info, success: CheckCircle2, warning: TriangleAlert, error: AlertCircle,
}
function icon(level: NotificationLevel) { return icons[level] ?? Bell }
function summary(item: NotificationItem): string { return item.title || item.message }

function shortLabel(item: NotificationItem): string {
  if (item.title) return item.title
  return item.message.length > 18 ? `${item.message.slice(0, 16)}…` : item.message
}

/* locale-aware relative time via Intl */
function relativeTime(ts: number): string {
  const diff = Math.round((ts - Date.now()) / 1000), abs = Math.abs(diff)
  let unit: Intl.RelativeTimeFormatUnit, val: number
  if (abs < 60) { unit = 'second'; val = diff }
  else if (abs < 3600) { unit = 'minute'; val = Math.round(diff / 60) }
  else if (abs < 86400) { unit = 'hour'; val = Math.round(diff / 3600) }
  else { unit = 'day'; val = Math.round(diff / 86400) }
  return new Intl.RelativeTimeFormat(locale.value, { numeric: 'auto' }).format(val, unit)
}

const host = ref<HTMLElement | null>(null)
// Hover auto-expands the enlarged island window (hover-intent delayed in the
// composable); clicking a capsule is the 通知打开 gesture → detail dialog.
function onEnter(item: NotificationItem) { hoverCapsuleEnter(item.id) }
function onLeave() { hoverCapsuleLeave() }
function onClickCapsule(item: NotificationItem) { openNotification(item.id) }

function onOutsideClick(e: MouseEvent) {
  if (openItem.value && !host.value?.contains(e.target as Node)) { closeOpen(); closeOverflow() }
}
function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape') { overflowOpen.value ? closeOverflow() : openItem.value ? closeOpen() : undefined }
}

onMounted(() => { document.addEventListener('mousedown', onOutsideClick); document.addEventListener('keydown', onKeydown) })
onBeforeUnmount(() => { document.removeEventListener('mousedown', onOutsideClick); document.removeEventListener('keydown', onKeydown); hoverCapsuleLeave() })

/* announce region — capped at 50 via FIFO */
const announcedItem = ref<NotificationItem | null>(null)
const MAX_ANNOUNCED = 50
const announcedIds = new Set<string>()
const announcedOrder: string[] = []
watch(
  () => items.value.map(i => i.id),
  () => {
    const newest = [...items.value].reverse().find(i => !announcedIds.has(i.id))
    if (!newest) return
    announcedIds.add(newest.id); announcedOrder.push(newest.id)
    if (announcedOrder.length > MAX_ANNOUNCED) { announcedIds.delete(announcedOrder.shift()!) }
    announcedItem.value = newest
  },
)

/* task helpers */
function isDetTask(item: NotificationItem) {
  return item.kind === 'task' && item.taskState === 'pending' && item.progress != null
}
function isIndetTask(item: NotificationItem) {
  return item.kind === 'task' && item.taskState === 'pending' && item.progress == null
}
function taskPct(item: NotificationItem) { return Math.round((item.progress ?? 0) * 100) }
function taskLabel(item: NotificationItem) {
  if (item.taskState === 'settled') {
    if (item.level === 'success') return t('app.taskSucceeded')
    if (item.level === 'error') return t('app.taskFailed')
  }
  return t('app.taskPending')
}
</script>

<template>
  <Teleport to="body">
    <div class="sr-only" aria-live="assertive" aria-atomic="true">
      {{ announcedItem?.level === 'error' ? announcedItem.message : '' }}
    </div>
    <div class="sr-only" aria-live="polite" aria-atomic="true">
      {{ announcedItem?.level !== 'error' ? announcedItem?.message : '' }}
    </div>

    <section
      v-if="items.length && routeKind !== 'pet'"
      ref="host"
      class="island-host"
      :class="`island-host--${routeKind}`"
      :aria-label="t('app.notificationRegion')"
      @mouseleave="onLeave"
    >
      <!-- STATUS ZONE — ongoing conditions, ALL rendered -->
      <TransitionGroup
        v-if="statusZone.length" tag="div" name="island-pop"
        class="island-zone island-zone--status" role="status" :aria-label="t('app.systemStatus')"
      >
        <div
          v-for="item in statusZone" :key="item.id"
          class="island-capsule island-capsule--status"
          :class="[
            `island-capsule--${item.level}`,
            openItem?.id === item.id ? 'island-capsule--active' : '',
            isDetTask(item) ? 'island-capsule--task-det' : '',
            isIndetTask(item) ? 'island-capsule--task-indet' : '',
          ]"
          @mouseenter="onEnter(item)"
        >
          <button
            type="button"
            class="island-capsule__main"
            :data-notification-id="item.id"
            :aria-label="summary(item) + (item.count > 1 ? `, ×${item.count}` : '')"
            :aria-expanded="openItem?.id === item.id"
            :aria-controls="openItem?.id === item.id ? 'notification-island-card' : undefined"
            :title="summary(item)"
            @mouseenter="onEnter(item)" @focusin="onEnter(item)"
            @click.stop="onClickCapsule(item)"
          >
            <component :is="icon(item.level)" :size="14" class="island-capsule__icon" aria-hidden="true" />
            <span class="island-capsule__text"><strong class="island-capsule__title">{{ item.title || shortLabel(item) }}</strong></span>
            <span v-if="item.count > 1" class="island-capsule__badge" :aria-label="t('app.repeatedCount', { count: item.count })">×{{ item.count }}</span>
            <span v-if="isDetTask(item)" class="island-capsule__progress" role="progressbar" :aria-valuenow="taskPct(item)" aria-valuemin="0" aria-valuemax="100">
              <span class="island-capsule__progress-fill" :style="{ width: taskPct(item) + '%' }" />
            </span>
            <LoaderCircle v-if="isIndetTask(item)" :size="12" class="island-capsule__spinner" aria-hidden="true" />
          </button>
          <!-- 超级岛-style residency marker: status conditions stay until their source clears them — no close affordance -->
          <Pin v-if="!isUserDismissible(item)" :size="10" class="island-capsule__pin" aria-hidden="true" />
          <button v-else type="button" class="island-capsule__close" :aria-label="t('app.close')" @click.stop="dismiss(item.id)"><X :size="10" aria-hidden="true" /></button>
        </div>
      </TransitionGroup>

      <!-- TRANSIENT ZONE — max 3, "just happened" -->
      <TransitionGroup
        v-if="transientZone.length" tag="div" name="island-pop"
        class="island-zone island-zone--transient"
      >
        <div
          v-for="item in transientZone" :key="item.id"
          class="island-capsule island-capsule--transient"
          :class="[
            `island-capsule--${item.level}`,
            openItem?.id === item.id ? 'island-capsule--active' : '',
            isDetTask(item) ? 'island-capsule--task-det' : '',
            isIndetTask(item) ? 'island-capsule--task-indet' : '',
          ]"
          @mouseenter="onEnter(item)"
        >
          <button
            type="button"
            class="island-capsule__main"
            :data-notification-id="item.id"
            :aria-label="summary(item) + (item.count > 1 ? `, ×${item.count}` : '')"
            :aria-expanded="openItem?.id === item.id"
            :aria-controls="openItem?.id === item.id ? 'notification-island-card' : undefined"
            :title="summary(item)"
            @mouseenter="onEnter(item)" @focusin="onEnter(item)"
            @click.stop="onClickCapsule(item)"
          >
            <component :is="icon(item.level)" :size="14" class="island-capsule__icon" aria-hidden="true" />
            <span class="island-capsule__text"><strong class="island-capsule__title">{{ shortLabel(item) }}</strong></span>
            <span v-if="item.count > 1" class="island-capsule__badge" :aria-label="t('app.repeatedCount', { count: item.count })">×{{ item.count }}</span>
          </button>
          <!-- sticky notices are persistent but user-owned — pin + close; pending tasks are handle-owned — pin only -->
          <Pin v-if="!isUserDismissible(item)" :size="10" class="island-capsule__pin" aria-hidden="true" />
          <button v-else type="button" class="island-capsule__close" :aria-label="t('app.close')" @click.stop="dismiss(item.id)"><X :size="10" aria-hidden="true" /></button>
        </div>
        <!-- overflow badge -->
        <button
          v-if="overflowCount > 0" key="__overflow__" type="button"
          class="island-capsule island-capsule--overflow"
          :class="overflowOpen ? 'island-capsule--active' : ''"
          :aria-label="t('app.more') + ` +${overflowCount}`"
          :aria-expanded="overflowOpen" aria-controls="notification-overflow-panel"
          @click.stop="toggleOverflow()"
        >
          <MoreHorizontal :size="14" class="island-capsule__icon" aria-hidden="true" />
          <span class="island-capsule__badge">+{{ overflowCount }}</span>
        </button>
      </TransitionGroup>

      <!-- DETAIL CARD — hover expands it; clicking the card itself opens the detail dialog (通知打开) -->
      <Transition name="island-card">
        <article
          v-if="openItem" id="notification-island-card" class="island-card no-drag"
          :class="`island-card--${openItem.level}`"
          @click="openNotification(openItem.id)"
          @mouseenter="hoverCardEnter()" @mouseleave="hoverCardLeave()"
        >
          <header class="island-card__header">
            <span class="island-card__icon" aria-hidden="true"><component :is="icon(openItem.level)" :size="17" /></span>
            <div class="island-card__heading">
              <strong>{{ openItem.title || summary(openItem) }}</strong>
              <span>{{ openItem.kind === 'task' ? taskLabel(openItem) : openItem.kind === 'status' ? t('app.systemStatus') : t('app.notification') }}</span>
            </div>
            <button type="button" class="island-card__icon-btn" :aria-label="t('app.close')" @click.stop="closeOpen()"><X :size="15" aria-hidden="true" /></button>
          </header>
          <div v-if="openItem.kind === 'task' && openItem.taskState === 'pending'" class="island-card__progress-wrap">
            <template v-if="openItem.progress != null">
              <div class="island-card__progress-track" role="progressbar" :aria-valuenow="taskPct(openItem)" aria-valuemin="0" aria-valuemax="100">
                <div class="island-card__progress-fill" :style="{ width: taskPct(openItem) + '%' }" />
              </div>
              <span class="island-card__progress-label">{{ taskPct(openItem) }}%</span>
            </template>
            <template v-else>
              <LoaderCircle :size="14" class="island-card__spinner" aria-hidden="true" />
              <span class="island-card__progress-label">{{ t('app.taskPending') }}</span>
            </template>
          </div>
          <!-- meta row: source, residency, age — the expanded presentation's leading context -->
          <div class="island-card__meta-row">
            <span v-if="openItem.source" class="island-card__chip">{{ openItem.source }}</span>
            <span v-if="openItem.persistence !== 'auto'" class="island-card__chip island-card__chip--persistent">
              <Pin :size="9" aria-hidden="true" />{{ t('app.persistent') }}
            </span>
            <time class="island-card__time" :datetime="new Date(openItem.createdAt).toISOString()">{{ relativeTime(openItem.createdAt) }}</time>
          </div>
          <p class="island-card__summary">{{ openItem.message }}</p>
          <p v-if="openItem.count > 1" class="island-card__meta">{{ t('app.repeatedCount', { count: openItem.count }) }}</p>
          <p v-if="openItem.remote" class="island-card__meta">{{ t('app.fromOtherWindow') }}</p>
          <!-- lifecycle hint replaces a close affordance the user does not own -->
          <p v-if="!isUserDismissible(openItem)" class="island-card__hint">
            {{ openItem.kind === 'status' ? t('app.statusAutoClear') : t('app.taskRunningHint') }}
          </p>
          <p v-if="actionStates[openItem.id]?.error" class="island-card__error" role="alert">
            <AlertCircle :size="14" aria-hidden="true" />{{ actionStates[openItem.id]?.error }}
          </p>
          <footer class="island-card__actions">
            <button v-if="isUserDismissible(openItem)" type="button" class="island-card__secondary" @click.stop="dismiss(openItem.id)"><X :size="14" aria-hidden="true" />{{ t('app.dismiss') }}</button>
            <button type="button" class="island-card__secondary" @click.stop="openDetail(openItem.id)"><FileText :size="14" aria-hidden="true" />{{ t('app.viewDetails') }}</button>
            <button v-if="openItem.action && !openItem.remote" type="button" class="island-card__primary" :disabled="actionStates[openItem.id]?.running" @click.stop="runAction(openItem.id)">
              <LoaderCircle v-if="actionStates[openItem.id]?.running" :size="14" class="island-card__spinner" aria-hidden="true" />
              <RotateCw v-else :size="14" aria-hidden="true" />{{ openItem.action.label }}
            </button>
          </footer>
        </article>
      </Transition>

      <!-- NOTIFICATION CENTER — 下拉小窗-style panel: live items above, cleared history below -->
      <Transition name="island-card">
        <article v-if="overflowOpen" id="notification-overflow-panel" class="island-overflow no-drag">
          <header class="island-overflow__header">
            <strong>{{ t('app.notificationCenter') }}</strong>
            <button type="button" class="island-card__icon-btn" :aria-label="t('app.close')" @click.stop="closeOverflow()"><X :size="15" aria-hidden="true" /></button>
          </header>
          <div class="island-overflow__scroll">
            <section v-if="orderedItems.length" class="island-overflow__section">
              <h3 class="island-overflow__heading">{{ t('app.activeSection') }}</h3>
              <ul class="island-overflow__list">
                <li v-for="item in orderedItems" :key="item.id" class="island-overflow__row">
                  <span class="island-overflow__icon" :class="`island-overflow__icon--${item.level}`">
                    <component :is="icon(item.level)" :size="13" aria-hidden="true" />
                  </span>
                  <div class="island-overflow__content">
                    <span class="island-overflow__title">
                      <Pin v-if="item.persistence !== 'auto'" :size="10" class="island-overflow__pin-inline" aria-hidden="true" />{{ item.title || item.message }}
                    </span>
                    <span v-if="item.source" class="island-overflow__source">{{ item.source }}</span>
                  </div>
                  <time class="island-overflow__time" :datetime="new Date(item.createdAt).toISOString()">{{ relativeTime(item.createdAt) }}</time>
                  <button type="button" class="island-overflow__action" :aria-label="t('app.viewDetails')" @click.stop="openDetail(item.id)"><FileText :size="12" aria-hidden="true" /></button>
                  <!-- source-owned rows show a residency marker instead of a close button -->
                  <span v-if="!isUserDismissible(item)" class="island-overflow__action island-overflow__action--static" :title="item.kind === 'status' ? t('app.statusAutoClear') : t('app.taskRunningHint')">
                    <Pin :size="12" aria-hidden="true" />
                  </span>
                  <button v-else type="button" class="island-overflow__action" :aria-label="t('app.dismiss')" @click.stop="dismiss(item.id)"><X :size="12" aria-hidden="true" /></button>
                </li>
              </ul>
            </section>
            <section v-if="history.length" class="island-overflow__section">
              <h3 class="island-overflow__heading">{{ t('app.clearedSection') }}</h3>
              <ul class="island-overflow__list">
                <li v-for="entry in history" :key="entry.id" class="island-overflow__row island-overflow__row--cleared">
                  <span class="island-overflow__icon" :class="`island-overflow__icon--${entry.level}`">
                    <component :is="icon(entry.level)" :size="13" aria-hidden="true" />
                  </span>
                  <div class="island-overflow__content">
                    <span class="island-overflow__title">{{ entry.title || entry.message }}</span>
                    <span v-if="entry.source" class="island-overflow__source">{{ entry.source }}</span>
                  </div>
                  <time class="island-overflow__time" :datetime="new Date(entry.dismissedAt).toISOString()">{{ relativeTime(entry.dismissedAt) }}</time>
                </li>
              </ul>
            </section>
          </div>
          <footer class="island-overflow__footer">
            <button type="button" class="island-card__secondary" :disabled="!history.length" @click.stop="clearHistory()">{{ t('app.clearHistory') }}</button>
            <button type="button" class="island-card__secondary" :disabled="!dismissibleLiveCount" @click.stop="dismissAll()">{{ t('app.dismissAll') }}</button>
          </footer>
        </article>
      </Transition>
    </section>
  </Teleport>
</template>

<style scoped>
/* ── HOST: per-route insets via custom properties ──
   Spans the full window width (left/right: 0) so the island column is truly
   centered under the title bar, Dynamic Island style. Symmetric inline padding
   of max(start, end) keeps content window-centered while still clearing the
   title-bar drag region and window controls on both sides. */
.island-host {
  position: fixed; left: 0; right: 0; z-index: 10050; pointer-events: none;
  display: flex; flex-direction: column; align-items: center;
  gap: 6px; container-type: inline-size;
  padding-inline: max(var(--island-inset-start), var(--island-inset-end));
  --island-inset-start: 0px; --island-inset-end: 0px;
  --island-spring: cubic-bezier(0.34, 1.45, 0.42, 1);
}
.island-host--standard { top: 6px; --island-inset-start: 200px; --island-inset-end: 140px; }
.island-host--detached { top: 42px; --island-inset-start: 8px; --island-inset-end: 8px; }
.island-host--debug { top: 78px; --island-inset-start: 12px; --island-inset-end: 12px; }

/* ── ZONES ── */
.island-zone {
  position: relative; /* containing block for absolutely-positioned leaving capsules */
  display: flex; align-items: center; justify-content: center; gap: 6px;
  max-width: 100%; pointer-events: none; flex-wrap: wrap;
}

/* ── CAPSULES ── */
.island-capsule {
  --level: var(--accent-primary, #2ec4b6);
  pointer-events: auto; -webkit-app-region: no-drag;
  display: flex; align-items: center; gap: 7px; height: 28px;
  min-width: 0; max-width: 160px; padding: 0 12px;
  border: 1px solid color-mix(in srgb, var(--level) 35%, var(--border-default, #1a1f29));
  border-radius: 999px;
  background: color-mix(in srgb, var(--bg-secondary, #11151c) 88%, var(--level));
  color: var(--text-primary, #c9d1d9);
  box-shadow: 0 1px 2px rgb(0 0 0 / 18%), 0 6px 18px rgb(0 0 0 / 22%);
  font: inherit; font-size: 11px; line-height: 1; overflow: hidden; position: relative;
  transition: max-width 200ms cubic-bezier(.2,.8,.2,1), padding 200ms cubic-bezier(.2,.8,.2,1),
    background-color 160ms ease, border-color 160ms ease, transform 160ms ease, box-shadow 160ms ease;
}
.island-capsule--status { background: color-mix(in srgb, var(--bg-secondary, #11151c) 82%, var(--level)); }
.island-capsule--transient { background: color-mix(in srgb, var(--bg-secondary, #11151c) 92%, var(--level)); }
.island-capsule--active {
  border-color: color-mix(in srgb, var(--level) 55%, var(--border-default, #1a1f29));
  background: color-mix(in srgb, var(--bg-secondary, #11151c) 72%, var(--level));
  box-shadow: 0 6px 20px rgb(0 0 0 / 28%);
}
.island-capsule--success { --level: var(--accent-success, #2ec4b6); }
.island-capsule--warning { --level: var(--accent-warning, #d29922); }
.island-capsule--error { --level: var(--accent-danger, #f85149); }
.island-capsule--info { --level: var(--accent-primary, #2ec4b6); }
.island-capsule__icon { flex: none; color: var(--level); }
.island-capsule__text { min-width: 0; display: flex; align-items: baseline; gap: 6px; overflow: hidden; white-space: nowrap; }
.island-capsule__title { overflow: hidden; text-overflow: ellipsis; font-weight: 600; }
.island-capsule__badge { flex: none; margin-left: 2px; font-weight: 700; color: var(--level); font-size: 10px; }

/* main pill button — transparent reset inside the container div */
.island-capsule__main {
  all: unset; box-sizing: border-box;
  display: flex; align-items: center; gap: 7px;
  min-width: 0; flex: 1 1 0;
  cursor: pointer;
  font: inherit; font-size: 11px; line-height: 1;
  color: var(--text-primary, #c9d1d9);
}

/* capsule close button — revealed on hover */
.island-capsule__close {
  flex: none; display: grid; place-items: center; width: 16px; height: 16px;
  padding: 0; margin-left: 2px; border: 0; border-radius: 50%;
  background: color-mix(in srgb, var(--level) 20%, transparent);
  color: var(--text-secondary, #7d8590); cursor: pointer;
  opacity: 0; transition: opacity 120ms ease;
}
.island-capsule:hover .island-capsule__close,
.island-capsule:focus-within .island-capsule__close { opacity: 1; }

/* residency marker — replaces the close button on source-owned capsules */
.island-capsule__pin {
  flex: none; margin-left: 2px; color: var(--level); opacity: 0.7;
}

.island-capsule__main:focus-visible { outline: 2px solid var(--level); outline-offset: 2px; }
.island-capsule__close:focus-visible { outline: 2px solid var(--level); outline-offset: 2px; }

/* task progress bar / spinner in capsule */
.island-capsule__progress {
  position: absolute; bottom: 0; left: 0; right: 0; height: 2px;
  background: color-mix(in srgb, var(--level) 12%, transparent); overflow: hidden;
}
.island-capsule__progress-fill { height: 100%; background: var(--level); transition: width 200ms ease; }
.island-capsule--task-det, .island-capsule--task-indet { position: relative; }
.island-capsule__spinner { flex: none; animation: island-spin 800ms linear infinite; color: var(--level); }

/* overflow badge */
.island-capsule--overflow {
  max-width: 48px; padding: 0 10px;
  background: color-mix(in srgb, var(--bg-secondary, #11151c) 85%, var(--text-secondary, #7d8590));
  border-color: var(--border-default, #1a1f29);
}
.island-capsule--overflow .island-capsule__icon { color: var(--text-secondary, #7d8590); }

/* ── DETAIL CARD ── */
.island-card {
  --level: var(--accent-primary, #2ec4b6); pointer-events: auto;
  width: min(340px, 100cqw); margin-top: 8px; padding: 13px;
  border: 1px solid color-mix(in srgb, var(--level) 32%, var(--border-default, #1a1f29));
  border-radius: 8px;
  background: color-mix(in srgb, var(--bg-primary, #0a0e14) 86%, transparent);
  color: var(--text-primary, #c9d1d9);
  box-shadow: 0 16px 44px rgb(0 0 0 / 42%);
  backdrop-filter: blur(18px) saturate(125%);
  -webkit-backdrop-filter: blur(18px) saturate(125%);
  -webkit-app-region: no-drag;
  cursor: pointer; /* the whole card opens the detail dialog */
}
.island-card:hover {
  border-color: color-mix(in srgb, var(--level) 55%, var(--border-default, #1a1f29));
}
.island-card--success { --level: var(--accent-success, #2ec4b6); }
.island-card--warning { --level: var(--accent-warning, #d29922); }
.island-card--error { --level: var(--accent-danger, #f85149); }
.island-card--info { --level: var(--accent-primary, #2ec4b6); }

.island-card__header { display: flex; align-items: center; gap: 9px; }
.island-card__icon {
  display: grid; place-items: center; width: 30px; height: 30px; flex: none;
  border-radius: 7px; background: color-mix(in srgb, var(--level) 14%, transparent); color: var(--level);
}
.island-card__heading { display: grid; min-width: 0; flex: 1; gap: 3px; }
.island-card__heading strong, .island-card__heading span {
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.island-card__heading strong { font-size: 12px; line-height: 1.25; }
.island-card__heading span { color: var(--text-tertiary, #656d76); font-size: 10px; }
.island-card__icon-btn {
  display: grid; place-items: center; width: 27px; height: 27px; flex: none;
  padding: 0; border: 0; border-radius: 6px; background: transparent;
  color: var(--text-secondary, #7d8590); cursor: pointer;
}
.island-card__icon-btn:hover, .island-card__secondary:hover {
  background: var(--bg-hover, #161b22); color: var(--text-primary, #c9d1d9);
}
.island-card__summary { margin: 11px 0 0; color: var(--text-secondary, #7d8590); font-size: 12px; line-height: 1.45; overflow-wrap: anywhere; }
.island-card__meta { margin: 5px 0 0; color: var(--text-tertiary, #656d76); font-size: 10px; line-height: 1.3; }

/* expanded-presentation meta row: source chip, residency chip, age */
.island-card__meta-row { display: flex; align-items: center; flex-wrap: wrap; gap: 6px; margin-top: 9px; }
.island-card__chip {
  display: inline-flex; align-items: center; gap: 3px;
  padding: 2px 7px; border-radius: 9px;
  background: color-mix(in srgb, var(--level) 10%, transparent);
  color: var(--text-secondary, #7d8590); font-size: 10px; line-height: 1.4; white-space: nowrap;
}
.island-card__chip--persistent { color: var(--level); }
.island-card__time { margin-left: auto; color: var(--text-tertiary, #656d76); font-size: 10px; white-space: nowrap; }
.island-card__hint {
  display: flex; align-items: center; gap: 5px; margin: 9px 0 0;
  color: var(--text-tertiary, #656d76); font-size: 10px; line-height: 1.4;
}
.island-card__progress-wrap { display: flex; align-items: center; gap: 8px; margin-top: 9px; }
.island-card__progress-track { flex: 1; height: 4px; border-radius: 2px; background: color-mix(in srgb, var(--level) 14%, transparent); overflow: hidden; }
.island-card__progress-fill { height: 100%; border-radius: 2px; background: var(--level); transition: width 200ms ease; }
.island-card__progress-label { flex: none; font-size: 10px; color: var(--text-tertiary, #656d76); }
.island-card__error {
  display: flex; align-items: flex-start; gap: 7px; margin: 9px 0 0; padding: 8px;
  border-radius: 6px; background: color-mix(in srgb, var(--accent-danger, #f85149) 10%, transparent);
  color: var(--accent-danger, #f85149); font-size: 11px; line-height: 1.4;
}
.island-card__error svg { flex: none; margin-top: 1px; }
.island-card__actions { display: flex; justify-content: flex-end; gap: 7px; margin-top: 12px; }
.island-card__actions button {
  display: inline-flex; align-items: center; justify-content: center; gap: 6px;
  min-height: 29px; padding: 5px 10px; border: 1px solid var(--border-default, #1a1f29);
  border-radius: 6px; font: inherit; font-size: 11px; cursor: pointer;
}
.island-card__secondary { background: transparent; color: var(--text-secondary, #7d8590); }
.island-card__primary {
  border-color: color-mix(in srgb, var(--level) 55%, transparent) !important;
  background: color-mix(in srgb, var(--level) 18%, var(--bg-secondary, #11151c));
  color: var(--level); font-weight: 600;
}
.island-card__actions button:disabled { opacity: 0.55; cursor: wait; }
.island-card__spinner { animation: island-spin 800ms linear infinite; }

/* ── OVERFLOW PANEL ── */
.island-overflow {
  pointer-events: auto; width: min(380px, 100cqw); max-height: 340px;
  margin-top: 8px; display: flex; flex-direction: column;
  border: 1px solid var(--border-default, #1a1f29); border-radius: 8px;
  background: color-mix(in srgb, var(--bg-primary, #0a0e14) 88%, transparent);
  color: var(--text-primary, #c9d1d9);
  box-shadow: 0 16px 44px rgb(0 0 0 / 42%);
  backdrop-filter: blur(18px) saturate(125%);
  -webkit-backdrop-filter: blur(18px) saturate(125%);
  -webkit-app-region: no-drag; overflow: hidden;
}
.island-overflow__header {
  display: flex; align-items: center; justify-content: space-between;
  padding: 10px 13px 8px; border-bottom: 1px solid var(--border-default, #1a1f29);
  font-size: 12px; font-weight: 600;
}
.island-overflow__scroll { flex: 1; overflow-y: auto; }
.island-overflow__section + .island-overflow__section { border-top: 1px solid color-mix(in srgb, var(--border-default, #1a1f29) 50%, transparent); }
.island-overflow__heading {
  margin: 0; padding: 8px 13px 3px;
  color: var(--text-tertiary, #656d76); font-size: 10px; font-weight: 600;
  letter-spacing: 0.04em; text-transform: uppercase;
}
.island-overflow__list { list-style: none; margin: 0; padding: 0 0 4px; }
.island-overflow__row {
  display: flex; align-items: center; gap: 8px; padding: 8px 13px;
  border-bottom: 1px solid color-mix(in srgb, var(--border-default, #1a1f29) 50%, transparent);
  font-size: 11px; transition: background 120ms ease;
}
.island-overflow__row:last-child { border-bottom: 0; }
.island-overflow__row:hover { background: var(--bg-hover, #161b22); }
.island-overflow__icon {
  flex: none; display: grid; place-items: center; width: 22px; height: 22px;
  border-radius: 5px; background: color-mix(in srgb, var(--accent-primary, #2ec4b6) 12%, transparent);
  color: var(--accent-primary, #2ec4b6);
}
.island-overflow__icon--success { background: color-mix(in srgb, var(--accent-success, #2ec4b6) 12%, transparent); color: var(--accent-success, #2ec4b6); }
.island-overflow__icon--warning { background: color-mix(in srgb, var(--accent-warning, #d29922) 12%, transparent); color: var(--accent-warning, #d29922); }
.island-overflow__icon--error { background: color-mix(in srgb, var(--accent-danger, #f85149) 12%, transparent); color: var(--accent-danger, #f85149); }
.island-overflow__content { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 1px; }
.island-overflow__title { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 600; }
.island-overflow__source { color: var(--text-tertiary, #656d76); font-size: 10px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.island-overflow__time { flex: none; color: var(--text-tertiary, #656d76); font-size: 10px; white-space: nowrap; }
.island-overflow__action {
  flex: none; display: grid; place-items: center; width: 22px; height: 22px;
  padding: 0; border: 0; border-radius: 4px; background: transparent;
  color: var(--text-secondary, #7d8590); cursor: pointer;
  opacity: 0; transition: opacity 120ms ease, background 120ms ease;
}
.island-overflow__row:hover .island-overflow__action { opacity: 1; }
.island-overflow__action:hover { background: var(--bg-hover, #161b22); color: var(--text-primary, #c9d1d9); }
/* static residency marker — same slot as the action buttons, never interactive */
.island-overflow__action--static, .island-overflow__action--static:hover {
  background: transparent; color: var(--text-tertiary, #656d76); cursor: default; opacity: 0.75;
}
.island-overflow__row:hover .island-overflow__action--static { opacity: 0.75; }
.island-overflow__pin-inline { margin-right: 3px; color: var(--text-tertiary, #656d76); vertical-align: -1px; }
.island-overflow__row--cleared { opacity: 0.66; }
.island-overflow__footer { display: flex; justify-content: flex-end; gap: 7px; padding: 8px 13px; border-top: 1px solid var(--border-default, #1a1f29); }
.island-overflow__footer button:disabled { opacity: 0.45; cursor: default; }
.island-overflow__footer button:disabled:hover { background: transparent; color: var(--text-secondary, #7d8590); }

/* ── TRANSITIONS ── */
/* Capsule pop — Dynamic Island style: expand from a compact pill with a
   slight overshoot, collapse away with a quick shrink. `move` keeps siblings
   gliding when a capsule enters/leaves the row. */
.island-pop-enter-active {
  transition: opacity 180ms ease, transform 260ms var(--island-spring);
}
.island-pop-leave-active { transition: opacity 130ms ease, transform 160ms ease; }
.island-pop-enter-from { opacity: 0; transform: translateY(-9px) scale(0.55); }
.island-pop-leave-to { opacity: 0; transform: scale(0.7); }
.island-pop-move { transition: transform 240ms var(--island-spring); }
.island-pop-leave-active.island-capsule { position: absolute; }

.island-card-enter-active { transition: opacity 160ms ease, transform 240ms var(--island-spring); transform-origin: top center; }
.island-card-leave-active { transition: opacity 140ms ease, transform 160ms ease; transform-origin: top center; }
.island-card-enter-from { opacity: 0; transform: translateY(-8px) scale(0.94); }
.island-card-leave-to { opacity: 0; transform: translateY(-5px) scale(0.98); }
@keyframes island-spin { to { transform: rotate(360deg); } }

/* ── CONTAINER QUERIES: icon-only when narrow ── */
@container (max-width: 420px) {
  .island-capsule__text { display: none; }
  .island-capsule { max-width: 32px; padding: 0 9px; }
}

@media (prefers-reduced-motion: reduce) {
  .island-capsule, .island-card-enter-active, .island-card-leave-active,
  .island-pop-enter-active, .island-pop-leave-active, .island-pop-move { transition: none; }
  .island-capsule__spinner, .island-card__spinner { animation: none; }
}

.sr-only {
  position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px;
  overflow: hidden; clip: rect(0, 0, 0, 0); white-space: nowrap; border: 0;
}
</style>
