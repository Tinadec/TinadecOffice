<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
} from 'reka-ui'
import { FolderOpen, FolderSync, LoaderCircle, RefreshCw, X } from '@lucide/vue'
import { UiBadge, UiButton, UiCheckbox } from '@/components/ui'
import {
  usePeerDevices,
  type PeerDeviceDto,
  type PeerRemoteWorkspaceDto,
  type PeerWorkspaceMountInput,
} from '@/composables/usePeerDevices'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { useNotifications } from '@/composables/useNotifications'
import { errorCodeMessage } from '@/lib/apiError'

const props = defineProps<{
  open: boolean
  device: PeerDeviceDto | null
}>()

const emit = defineEmits<{
  'update:open': [value: boolean]
  saved: [workspaces: PeerRemoteWorkspaceDto[]]
}>()

const { t, te } = useI18n()
const { getPanelDataAttributes } = usePanelStyles()
const { notify } = useNotifications()
const { fetchRemoteWorkspaces, mountRemoteWorkspaces, refreshAll } = usePeerDevices()

const loading = ref(false)
const saving = ref(false)
const inlineError = ref<string | null>(null)
const remoteWorkspaces = ref<PeerRemoteWorkspaceDto[]>([])
const selectedIds = ref<Set<string>>(new Set())

const busy = computed(() => loading.value || saving.value)
const selectedCount = computed(() => selectedIds.value.size)

function formatError(err: unknown): string {
  const code = (err as { code?: string | null })?.code
  const mapped = errorCodeMessage(code, undefined, { t, te })
  if (mapped) return mapped
  return err instanceof Error ? err.message : String(err)
}

async function loadWorkspaces() {
  const dev = props.device
  if (!dev) return
  loading.value = true
  inlineError.value = null
  try {
    const rows = await fetchRemoteWorkspaces(dev.device_id)
    remoteWorkspaces.value = rows
    selectedIds.value = new Set(
      rows
        .filter((ws) => ws.mounted && ws.visible !== false)
        .map((ws) => ws.remote_project_id),
    )
  } catch (err) {
    remoteWorkspaces.value = []
    inlineError.value = formatError(err)
  } finally {
    loading.value = false
  }
}

watch(
  () => [props.open, props.device?.device_id] as const,
  ([isOpen, deviceId]) => {
    if (isOpen && deviceId) {
      void loadWorkspaces()
    } else if (!isOpen) {
      inlineError.value = null
      remoteWorkspaces.value = []
      selectedIds.value = new Set()
    }
  },
  { immediate: true },
)

function toggleWorkspace(remoteProjectId: string, checked?: boolean) {
  const next = new Set(selectedIds.value)
  const shouldSelect = checked ?? !next.has(remoteProjectId)
  if (shouldSelect) next.add(remoteProjectId)
  else next.delete(remoteProjectId)
  selectedIds.value = next
}

function selectAll() {
  selectedIds.value = new Set(remoteWorkspaces.value.map((ws) => ws.remote_project_id))
}

function clearAll() {
  selectedIds.value = new Set()
}

function handleOpenChange(next: boolean) {
  if (saving.value && !next) return
  emit('update:open', next)
}

async function handleSave() {
  const dev = props.device
  if (!dev || saving.value) return
  saving.value = true
  inlineError.value = null
  try {
    const mounts: PeerWorkspaceMountInput[] = remoteWorkspaces.value.map((ws) => {
      const isMounted = selectedIds.value.has(ws.remote_project_id)
      return {
        remote_project_id: ws.remote_project_id,
        name: ws.name,
        remote_root_path: ws.remote_root_path,
        mounted: isMounted,
        visible: isMounted,
      }
    })
    const updated = await mountRemoteWorkspaces(dev.device_id, mounts)
    await refreshAll().catch(() => {})
    notify.success(t('peers.mountModal.savedToast', { name: dev.display_name }))
    emit('saved', updated)
    emit('update:open', false)
  } catch (err) {
    inlineError.value = formatError(err)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <DialogRoot :open="open" @update:open="handleOpenChange">
    <DialogPortal>
      <DialogOverlay class="peer-modal-overlay" />
      <!-- IMPORTANT: Centered via fixed inset + margin:auto without any ancestor or self transform,
           preserving backdrop-filter compositing across all Chromium/Electron material presets. -->
      <DialogContent
        class="peer-modal-surface"
        v-bind="getPanelDataAttributes()"
        data-testid="peer-mount-workspace-modal"
        @escape-key-down="saving && $event.preventDefault()"
        @interact-outside="saving && $event.preventDefault()"
      >
        <header class="peer-modal-header">
          <div class="peer-modal-heading">
            <FolderSync :size="18" class="peer-modal-icon" aria-hidden="true" />
            <div>
              <DialogTitle class="peer-modal-title">
                {{ t('peers.mountModal.title', { name: device?.display_name ?? '' }) }}
              </DialogTitle>
              <DialogDescription class="peer-modal-desc">
                {{ t('peers.mountModal.description') }}
              </DialogDescription>
            </div>
          </div>
          <div class="peer-modal-head-actions">
            <UiButton
              type="button"
              variant="ghost"
              size="icon"
              :aria-label="t('common.refresh')"
              :disabled="busy"
              @click="loadWorkspaces"
            >
              <RefreshCw :size="14" />
            </UiButton>
            <DialogClose as-child>
              <UiButton type="button" variant="ghost" size="icon" :aria-label="t('common.close')" :disabled="saving">
                <X :size="16" />
              </UiButton>
            </DialogClose>
          </div>
        </header>

        <div class="peer-mount-body">
          <div v-if="loading" class="peer-mount-status" role="status">
            <LoaderCircle :size="15" class="animate-spin" />
            <span>{{ t('peers.mountModal.loading') }}</span>
          </div>

          <template v-else-if="remoteWorkspaces.length > 0">
            <div class="peer-mount-toolbar">
              <UiButton type="button" variant="ghost" size="sm" :disabled="saving" @click="selectAll">
                {{ t('peers.mountModal.selectAll') }}
              </UiButton>
              <UiButton type="button" variant="ghost" size="sm" :disabled="saving" @click="clearAll">
                {{ t('peers.mountModal.clearAll') }}
              </UiButton>
            </div>

            <ul class="peer-mount-list" role="list">
              <li
                v-for="ws in remoteWorkspaces"
                :key="ws.remote_project_id"
                class="peer-mount-item"
                :class="{ 'is-selected': selectedIds.has(ws.remote_project_id) }"
                :data-testid="`peer-mount-row-${ws.remote_project_id}`"
                @click="toggleWorkspace(ws.remote_project_id)"
              >
                <UiCheckbox
                  :model-value="selectedIds.has(ws.remote_project_id)"
                  :aria-label="ws.name"
                  :disabled="saving"
                  @update:model-value="toggleWorkspace(ws.remote_project_id, $event)"
                />
                <FolderOpen :size="15" class="peer-mount-folder-icon" aria-hidden="true" />
                <div class="peer-mount-meta">
                  <strong>{{ ws.name }}</strong>
                  <code v-if="ws.remote_root_path">{{ ws.remote_root_path }}</code>
                </div>
                <UiBadge v-if="ws.mounted" variant="outline">{{ t('peers.mountModal.mountedBadge') }}</UiBadge>
              </li>
            </ul>
          </template>

          <p v-else-if="!inlineError" class="peer-mount-empty">
            {{ t('peers.mountModal.empty') }}
          </p>

          <p v-if="inlineError" class="peer-modal-error" role="alert">
            {{ inlineError }}
          </p>
        </div>

        <footer class="peer-modal-footer">
          <UiButton type="button" variant="ghost" size="sm" :disabled="saving" @click="handleOpenChange(false)">
            {{ t('common.cancel') }}
          </UiButton>
          <UiButton
            type="button"
            size="sm"
            :disabled="busy || remoteWorkspaces.length === 0"
            data-testid="peer-mount-save-btn"
            @click="handleSave"
          >
            <LoaderCircle v-if="saving" :size="14" class="animate-spin" />
            <FolderSync v-else :size="14" />
            <span>{{ saving ? t('peers.mountModal.saving') : t('peers.mountModal.saveBtn', { count: selectedCount }) }}</span>
          </UiButton>
        </footer>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>

<style scoped>
.peer-modal-overlay {
  position: fixed;
  inset: 0;
  z-index: 90;
  background: color-mix(in srgb, var(--bg-overlay, #09090b) 62%, transparent);
  transition: opacity 160ms ease;
}

.peer-modal-surface {
  position: fixed;
  inset: 0;
  z-index: 91;
  margin: auto;
  width: min(520px, calc(100vw - 32px));
  height: fit-content;
  max-height: calc(100vh - 48px);
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 20px;
  border-radius: 12px;
  border: 1px solid var(--material-floating-border, var(--border-card));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  color: var(--text-primary);
  box-shadow: var(--shadow-card-subtle);
  transition: opacity 160ms ease;
}

.peer-modal-surface:focus-visible {
  outline: 2px solid var(--focus-ring-color, var(--accent-primary));
  outline-offset: 2px;
}

.peer-modal-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.peer-modal-heading {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  min-width: 0;
}

.peer-modal-head-actions {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.peer-modal-icon {
  flex-shrink: 0;
  margin-top: 2px;
  color: var(--text-primary);
}

.peer-modal-title {
  margin: 0;
  font-size: 15px;
  font-weight: 600;
}

.peer-modal-desc {
  margin: 4px 0 0;
  font-size: 12px;
  line-height: 1.5;
  color: var(--text-secondary);
}

.peer-mount-body {
  display: flex;
  flex-direction: column;
  gap: 10px;
  min-height: 120px;
}

.peer-mount-status {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 16px;
  color: var(--text-secondary);
  font-size: 12.5px;
}

.peer-mount-toolbar {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 6px;
}

.peer-mount-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
  max-height: 300px;
  overflow-y: auto;
}

.peer-mount-item {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 12px;
  border-radius: 8px;
  border: 1px solid var(--border-card);
  background: var(--surface-section);
  cursor: pointer;
}

.peer-mount-item:hover {
  background: var(--surface-hover);
}

.peer-mount-item.is-selected {
  border-color: var(--border-card-active, var(--border-default));
  background: var(--surface-raised);
}

.peer-mount-folder-icon {
  flex-shrink: 0;
  color: var(--text-secondary);
}

.peer-mount-meta {
  display: flex;
  flex-direction: column;
  gap: 2px;
  flex: 1;
  min-width: 0;
}

.peer-mount-meta strong {
  font-size: 13px;
  font-weight: 500;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.peer-mount-meta code {
  font-family: var(--font-mono);
  font-size: 11px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.peer-mount-empty {
  margin: 0;
  padding: 18px 14px;
  border-radius: 8px;
  border: 1px dashed var(--border-dashed, var(--border-card));
  color: var(--text-muted);
  font-size: 12.5px;
  text-align: center;
}

.peer-modal-error {
  margin: 0;
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px solid color-mix(in srgb, var(--accent-danger) 40%, transparent);
  background: color-mix(in srgb, var(--accent-danger) 8%, var(--surface-section));
  color: var(--accent-danger);
  font-size: 12px;
}

.peer-modal-footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
}

@media (prefers-reduced-motion: reduce) {
  .peer-modal-overlay,
  .peer-modal-surface {
    transition: none;
  }
}
</style>
