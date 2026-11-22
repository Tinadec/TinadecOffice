<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Plus, RefreshCw, Save, Trash2, Webhook } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiLabel, UiSelectField } from '@/components/ui'
import {
  api as baseApi,
  type LifecycleHookEntryDto,
  type LifecycleHookEvent,
  type LifecycleHookScope,
} from '@/api'
import { scopedApi } from '@/lib/storageScope'
import { useNotifications } from '@/composables/useNotifications'
import { useHostAccess } from '@/lib/hostAccess'

const api = scopedApi(baseApi, () => 'user')

const emit = defineEmits<{
  changed: []
}>()

const { t } = useI18n()
const { notify } = useNotifications()
const { canAccessBackend } = useHostAccess()

const activeScope = ref<LifecycleHookScope>('workspace')
const workspaceRoot = ref<string | null>(null)
const hooks = ref<LifecycleHookEntryDto[]>([])
const baselineJson = ref('[]')
const revision = ref<number | null>(null)
const loading = ref(false)
const saving = ref(false)
const loadError = ref<string | null>(null)

const eventOptions = computed(() => [
  { value: 'PreToolUse', label: t('settings.lifecycleHooks.eventPreToolUse') },
  { value: 'PostToolUse', label: t('settings.lifecycleHooks.eventPostToolUse') },
  { value: 'OnTaskComplete', label: t('settings.lifecycleHooks.eventOnTaskComplete') },
  { value: 'OnSessionIdle', label: t('settings.lifecycleHooks.eventOnSessionIdle') },
])

function normalizeHook(entry: LifecycleHookEntryDto): LifecycleHookEntryDto {
  const timeout = Number.isFinite(entry.timeout_ms) && entry.timeout_ms > 0
    ? Math.round(entry.timeout_ms)
    : 5000
  return {
    id: entry.id,
    name: entry.name?.trim() ? entry.name.trim() : null,
    event: entry.event,
    matcher: entry.matcher?.trim() ? entry.matcher.trim() : null,
    command: entry.command,
    timeout_ms: timeout,
    blocking: Boolean(entry.blocking),
    enabled: entry.enabled !== false,
  }
}

const serializedCurrent = computed(() => JSON.stringify(hooks.value.map(normalizeHook)))
const isDirty = computed(() => serializedCurrent.value !== baselineJson.value)
const hasEmptyCommand = computed(() => hooks.value.some((h) => !h.command.trim()))

function newHookId(): string {
  const suffix = globalThis.crypto?.randomUUID?.().slice(0, 8) ?? Math.random().toString(36).slice(2, 10)
  return `hook-${suffix}`
}

async function loadHooks() {
  if (!canAccessBackend.value || typeof api.listLifecycleHooks !== 'function') return
  loading.value = true
  loadError.value = null
  try {
    const doc = await api.listLifecycleHooks({ scope: activeScope.value })
    const list = Array.isArray(doc?.hooks) ? doc.hooks.map(normalizeHook) : []
    hooks.value = list
    baselineJson.value = JSON.stringify(list)
    revision.value = typeof doc?.revision === 'number' ? doc.revision : null
    workspaceRoot.value = doc?.workspace_root ?? null
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : String(err)
  } finally {
    loading.value = false
  }
}

function setScope(scope: LifecycleHookScope) {
  if (activeScope.value === scope) return
  activeScope.value = scope
  void loadHooks()
}

function addHook() {
  hooks.value = [
    ...hooks.value,
    {
      id: newHookId(),
      name: null,
      event: 'PreToolUse',
      matcher: null,
      command: '',
      timeout_ms: 5000,
      blocking: true,
      enabled: true,
    },
  ]
}

function updateHook(id: string, patch: Partial<LifecycleHookEntryDto>) {
  hooks.value = hooks.value.map((item) =>
    item.id === id ? { ...item, ...patch } : item,
  )
}

function removeHook(id: string) {
  hooks.value = hooks.value.filter((item) => item.id !== id)
}

async function saveHooks() {
  if (!canAccessBackend.value || saving.value || hasEmptyCommand.value) return
  saving.value = true
  try {
    const payloadHooks = hooks.value.map((h) => ({
      ...normalizeHook(h),
      command: h.command.trim(),
    }))
    const saved = await api.saveLifecycleHooks(
      {
        scope: activeScope.value,
        workspace_root: workspaceRoot.value,
        hooks: payloadHooks,
        expected_revision: revision.value,
      },
      revision.value,
    )
    const nextList = Array.isArray(saved?.hooks) ? saved.hooks.map(normalizeHook) : payloadHooks
    hooks.value = nextList
    baselineJson.value = JSON.stringify(nextList)
    revision.value = typeof saved?.revision === 'number' ? saved.revision : revision.value
    notify.success({ message: t('settings.lifecycleHooks.saved') })
    emit('changed')
  } catch (err) {
    notify.error(err)
  } finally {
    saving.value = false
  }
}

watch(canAccessBackend, (ready) => {
  if (ready) void loadHooks()
})

onMounted(() => {
  if (canAccessBackend.value) {
    void loadHooks()
  }
})

defineExpose({ reload: loadHooks })
</script>

<template>
  <section class="center-resource-section lifecycle-hooks-panel" data-testid="lifecycle-hooks-panel">
    <header class="lh-head">
      <div class="lh-title">
        <div class="lh-title-row">
          <Webhook :size="16" />
          <h3>{{ t('settings.lifecycleHooks.title') }}</h3>
          <UiBadge variant="outline">{{ hooks.length }}</UiBadge>
        </div>
        <p>{{ t('settings.lifecycleHooks.subtitle') }}</p>
      </div>

      <div class="lh-actions">
        <div class="lh-scope-tabs" role="tablist" :aria-label="t('settings.lifecycleHooks.scopeField')">
          <button
            type="button"
            role="tab"
            data-testid="lifecycle-scope-workspace"
            :aria-selected="activeScope === 'workspace'"
            :class="{ active: activeScope === 'workspace' }"
            @click="setScope('workspace')"
          >
            {{ t('settings.lifecycleHooks.scopeWorkspace') }}
          </button>
          <button
            type="button"
            role="tab"
            data-testid="lifecycle-scope-global"
            :aria-selected="activeScope === 'global'"
            :class="{ active: activeScope === 'global' }"
            @click="setScope('global')"
          >
            {{ t('settings.lifecycleHooks.scopeGlobal') }}
          </button>
        </div>

        <UiButton
          size="sm"
          variant="outline"
          data-testid="lifecycle-hook-add"
          :disabled="!canAccessBackend || loading || saving"
          @click="addHook"
        >
          <Plus :size="14" />
          <span>{{ t('settings.lifecycleHooks.addHook') }}</span>
        </UiButton>

        <UiButton
          size="sm"
          data-testid="lifecycle-hook-save"
          :disabled="!canAccessBackend || loading || saving || !isDirty || hasEmptyCommand"
          @click="saveHooks"
        >
          <Save :size="14" />
          <span>{{ t('settings.lifecycleHooks.saveHooks') }}</span>
        </UiButton>

        <UiButton
          size="sm"
          variant="ghost"
          :disabled="!canAccessBackend || loading || saving"
          :aria-label="t('settings.refresh')"
          @click="loadHooks"
        >
          <RefreshCw :size="14" />
        </UiButton>
      </div>
    </header>

    <div v-if="loadError" class="lh-error" role="alert">
      <span>{{ loadError }}</span>
      <UiButton size="sm" variant="outline" @click="loadHooks">{{ t('settings.retry') }}</UiButton>
    </div>

    <div v-else-if="hooks.length === 0" class="lh-empty quiet">
      {{ t('settings.lifecycleHooks.empty') }}
    </div>

    <div v-else class="lh-list" role="list">
      <div
        v-for="hook in hooks"
        :key="hook.id"
        class="lh-card"
        role="listitem"
        :data-testid="`lifecycle-hook-row-${hook.id}`"
      >
        <div class="lh-grid">
          <div class="lh-field">
            <UiLabel>{{ t('settings.lifecycleHooks.eventField') }}</UiLabel>
            <UiSelectField
              :model-value="hook.event"
              :options="eventOptions"
              @update:model-value="(val: string) => updateHook(hook.id, { event: val as LifecycleHookEvent })"
            />
          </div>

          <div class="lh-field">
            <UiLabel>{{ t('settings.lifecycleHooks.matcherField') }}</UiLabel>
            <UiInput
              :model-value="hook.matcher ?? ''"
              :placeholder="t('settings.lifecycleHooks.matcherPlaceholder')"
              @update:model-value="(val: string | number) => updateHook(hook.id, { matcher: String(val) || null })"
            />
          </div>

          <div class="lh-field lh-field-wide">
            <UiLabel>{{ t('settings.lifecycleHooks.commandField') }}</UiLabel>
            <UiInput
              :data-testid="`lifecycle-hook-command-${hook.id}`"
              :model-value="hook.command"
              :placeholder="t('settings.lifecycleHooks.commandPlaceholder')"
              @update:model-value="(val: string | number) => updateHook(hook.id, { command: String(val) })"
            />
          </div>

          <div class="lh-field lh-field-timeout">
            <UiLabel>{{ t('settings.lifecycleHooks.timeoutField') }}</UiLabel>
            <UiInput
              type="number"
              :model-value="String(hook.timeout_ms)"
              @update:model-value="(val: string | number) => updateHook(hook.id, { timeout_ms: Math.max(100, Number(val) || 5000) })"
            />
          </div>
        </div>

        <div class="lh-card-foot">
          <div class="lh-switches">
            <button
              type="button"
              role="switch"
              class="lh-pill-switch"
              :data-testid="`lifecycle-hook-enabled-${hook.id}`"
              :aria-checked="hook.enabled"
              :class="{ active: hook.enabled }"
              @click="updateHook(hook.id, { enabled: !hook.enabled })"
            >
              {{ t('settings.lifecycleHooks.enabledLabel') }}
            </button>

            <button
              type="button"
              role="switch"
              class="lh-pill-switch"
              :data-testid="`lifecycle-hook-blocking-${hook.id}`"
              :aria-checked="hook.blocking"
              :class="{ active: hook.blocking }"
              @click="updateHook(hook.id, { blocking: !hook.blocking })"
            >
              {{ t('settings.lifecycleHooks.blockingLabel') }}
            </button>
          </div>

          <UiButton
            size="sm"
            variant="ghost"
            class="lh-delete-btn"
            :data-testid="`lifecycle-hook-delete-${hook.id}`"
            :aria-label="t('settings.deleteAction')"
            @click="removeHook(hook.id)"
          >
            <Trash2 :size="14" />
            <span>{{ t('settings.deleteAction') }}</span>
          </UiButton>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.lifecycle-hooks-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 14px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

.lh-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 10px;
}

.lh-title {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.lh-title-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.lh-title-row h3 {
  margin: 0;
  font-size: 14px;
}

.lh-title p {
  margin: 0;
  font-size: 12px;
  color: var(--text-secondary);
}

.lh-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
}

.lh-scope-tabs {
  display: inline-flex;
  gap: 2px;
  padding: 2px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.lh-scope-tabs button {
  padding: 4px 10px;
  border: 0;
  border-radius: var(--radius-control, 6px);
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  white-space: nowrap;
  flex-shrink: 0;
  cursor: pointer;
}

.lh-scope-tabs button:focus-visible,
.lh-pill-switch:focus-visible {
  outline: var(--focus-ring-width, 2px) solid var(--focus-ring-color, var(--accent-primary));
  outline-offset: 2px;
}

.lh-scope-tabs button.active {
  background: var(--surface-raised);
  color: var(--text-primary);
  font-weight: 500;
}

.lh-error {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px solid color-mix(in srgb, var(--accent-danger, #ef4444) 35%, var(--border-muted));
  color: var(--accent-danger, #ef4444);
  font-size: 12px;
}

.lh-empty {
  padding: 14px 8px;
  text-align: center;
}

.lh-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.lh-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px;
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.lh-grid {
  display: grid;
  grid-template-columns: minmax(170px, 1fr) minmax(180px, 1.2fr) minmax(220px, 1.8fr) 110px;
  gap: 10px;
}

.lh-field {
  display: flex;
  flex-direction: column;
  gap: 5px;
  min-width: 0;
}

.lh-card-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 8px;
}

.lh-switches {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.lh-pill-switch {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  min-height: var(--control-height-xs, 26px);
  padding: 2px 10px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 11px;
  white-space: nowrap;
  cursor: pointer;
  transition: border-color 120ms ease, background-color 120ms ease, color 120ms ease;
}

.lh-pill-switch.active {
  border-color: var(--accent-primary);
  background: var(--surface-selected);
  color: var(--text-primary);
  font-weight: 500;
}

.lh-delete-btn {
  color: var(--accent-danger, #ef4444);
}

.quiet {
  color: var(--text-muted);
  font-size: 12px;
}

@media (max-width: 960px) {
  .lh-grid {
    grid-template-columns: 1fr 1fr;
  }
  .lh-field-wide {
    grid-column: 1 / -1;
  }
}
</style>
