<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, Folder, FolderOpen, X } from '@lucide/vue'
import { UiButton, UiSwitch } from '@/components/ui'
import { homeController } from '@/controllers/HomeController'
import { useHostAccess } from '@/lib/hostAccess'
import { canPickHostDirectories, pickWorkspaceFolders } from '@/lib/hostConnection'
import { selectionKey } from '@/lib/storageScope'
import { queueDeferredHabit, useOobe } from '@/composables/useOobe'

const { t, te } = useI18n()
const { isPreview } = useHostAccess()
const { selectedWorkspacePath, deferredHabits } = useOobe()

function label(key: string, fallback: string): string {
  return te(key) ? String(t(key)) : fallback
}

const canPickFolder = computed(() => !isPreview.value && canPickHostDirectories())

const isElectronCompanionSupported = computed(() => {
  if (typeof window === 'undefined') return false
  const bridge = window.tinadec as (typeof window.tinadec & { isWebShim?: boolean }) | undefined
  return Boolean(bridge && !bridge.isWebShim && !isPreview.value && typeof bridge.saveCompanionMode === 'function')
})

const activeProjects = computed(() =>
  (homeController.projects?.value ?? [])
    .filter((p) => p.lifecycle_status !== 'archived' && p.lifecycle_status !== 'trashed')
    .slice(0, 5),
)

const currentFolder = computed(() =>
  selectedWorkspacePath.value || homeController.currentProject?.value?.path || '',
)

const pickingFolder = ref(false)
const folderError = ref('')
const companionEnabled = ref<boolean>(deferredHabits.value.companionCapsule ?? false)

async function pickFolder(): Promise<void> {
  if (!canPickFolder.value) return
  pickingFolder.value = true
  folderError.value = ''
  try {
    const paths = await pickWorkspaceFolders(label('oobe.workspace.title', '选择工作区'), { multiple: false })
    const chosen = paths?.[0]?.trim()
    if (!chosen) return
    selectedWorkspacePath.value = chosen

    const existing = (homeController.projects?.value ?? []).find(
      (item) => item.path?.toLowerCase() === chosen.toLowerCase(),
    )
    if (existing) {
      homeController.setSelectedProject?.(selectionKey(existing))
      return
    }

    try {
      const { api } = await import('@/api')
      if (typeof api.createProject === 'function') {
        const name = chosen.split(/[\\/]/).filter(Boolean).pop() || 'Workspace'
        const created = await api.createProject(name, chosen)
        if (created?.id) {
          await homeController.refreshProjectsAndSessions?.().catch(() => undefined)
          homeController.setSelectedProject?.(selectionKey(created))
        }
      }
    } catch {
      // Path remains displayed if already registered concurrently.
    }
  } catch (err) {
    folderError.value = err instanceof Error ? err.message : String(err)
  } finally {
    pickingFolder.value = false
  }
}

function selectExistingProject(project: { id: string; path: string; storage_id?: string | null }): void {
  selectedWorkspacePath.value = project.path
  homeController.setSelectedProject?.(selectionKey(project))
}

function clearSelectedWorkspace(): void {
  selectedWorkspacePath.value = ''
  homeController.setSelectedProject?.(null)
}

function onToggleCompanion(next: boolean): void {
  companionEnabled.value = next
  queueDeferredHabit('companionCapsule', next)
}
</script>

<template>
  <section class="oobe-stage-panel" data-testid="oobe-step-workspace">
    <header class="oobe-stage-head">
      <span class="oobe-eyebrow">03 / 03</span>
      <h1>{{ label('oobe.workspace.title', '打开你的首个工作区') }}</h1>
      <p>{{ label('oobe.workspace.subtitle', '绑定一个代码仓库或文档目录开始项目协作；也可以留空，直接进入自由对话。') }}</p>
    </header>

    <div class="oobe-folder-picker-box">
      <div class="oobe-folder-display">
        <FolderOpen :size="15" class="oobe-folder-icon" aria-hidden="true" />
        <span
          class="oobe-folder-path"
          :class="{ 'is-placeholder': !currentFolder }"
          :title="currentFolder || undefined"
          data-testid="oobe-selected-folder"
        >
          {{ currentFolder || label('oobe.workspace.emptyPlaceholder', '尚未绑定目录（进入后开启自由对话）') }}
        </span>
      </div>

      <div class="oobe-folder-actions">
        <button
          v-if="currentFolder"
          type="button"
          class="oobe-clear-folder"
          :title="label('common.clear', '清除')"
          :aria-label="label('common.clear', '清除')"
          data-testid="oobe-clear-folder"
          @click="clearSelectedWorkspace"
        >
          <X :size="13" aria-hidden="true" />
        </button>

        <UiButton
          v-if="canPickFolder"
          variant="outline"
          size="sm"
          :disabled="pickingFolder"
          data-testid="oobe-pick-folder-btn"
          @click="pickFolder"
        >
          <FolderOpen :size="14" />
          <span>{{ label('oobe.workspace.browseButton', '选择文件夹…') }}</span>
        </UiButton>
      </div>
    </div>

    <p v-if="folderError" class="oobe-inline-error" role="alert">{{ folderError }}</p>

    <div v-if="activeProjects.length > 0" class="oobe-recent-block" data-testid="oobe-existing-projects">
      <span class="oobe-section-caption">{{ label('oobe.workspace.recentTitle', '已有工作区') }}</span>
      <ul class="oobe-project-list">
        <li v-for="project in activeProjects" :key="project.id">
          <button
            type="button"
            class="oobe-project-row"
            :class="{ 'is-selected': currentFolder && currentFolder.toLowerCase() === project.path.toLowerCase() }"
            :data-testid="`oobe-project-${project.id}`"
            @click="selectExistingProject(project)"
          >
            <Folder :size="14" class="oobe-project-icon" aria-hidden="true" />
            <div class="oobe-project-copy">
              <strong>{{ project.name }}</strong>
              <span>{{ project.path }}</span>
            </div>
            <Check
              v-if="currentFolder && currentFolder.toLowerCase() === project.path.toLowerCase()"
              :size="14"
              class="oobe-project-check"
              aria-hidden="true"
            />
          </button>
        </li>
      </ul>
    </div>

    <div v-if="isElectronCompanionSupported" class="oobe-companion-row">
      <div class="oobe-companion-copy">
        <span class="oobe-companion-label">{{ label('oobe.habits.companionLabel', '启用桌面悬浮伙伴胶囊') }}</span>
        <span class="oobe-companion-desc">{{ label('oobe.workspace.companionHint', '进入工作台后在屏幕边缘驻留轻量快捷入口') }}</span>
      </div>
      <UiSwitch
        :model-value="companionEnabled"
        :aria-label="label('oobe.habits.companionLabel', '启用桌面悬浮伙伴胶囊')"
        data-testid="oobe-companion-switch"
        @update:model-value="onToggleCompanion(Boolean($event))"
      />
    </div>
  </section>
</template>

<style scoped>
.oobe-stage-panel {
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.oobe-stage-head {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.oobe-eyebrow {
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 11px;
  font-weight: 500;
  letter-spacing: 0.08em;
  color: var(--text-muted);
}

.oobe-stage-head h1 {
  margin: 0;
  font-size: 24px;
  font-weight: 600;
  letter-spacing: -0.025em;
  line-height: 1.25;
  color: var(--text-primary);
}

.oobe-stage-head p {
  margin: 2px 0 0;
  font-size: 13.5px;
  line-height: 1.6;
  color: var(--text-secondary);
}

.oobe-folder-picker-box {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 12px 14px;
  border-radius: 10px;
  border: 1px solid var(--border-default, var(--border-muted));
  background: var(--surface-section);
}

.oobe-folder-display {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
  flex: 1;
}

.oobe-folder-icon {
  flex-shrink: 0;
  color: var(--text-secondary);
}

.oobe-folder-path {
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 12.5px;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.oobe-folder-path.is-placeholder {
  font-family: inherit;
  color: var(--text-muted);
}

.oobe-folder-actions {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
}

.oobe-clear-folder {
  width: 26px;
  height: 26px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-muted);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
}

.oobe-clear-folder:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.oobe-inline-error {
  margin: -8px 0 0;
  font-size: 12px;
  color: var(--accent-danger);
}

.oobe-recent-block {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.oobe-section-caption {
  font-size: 11.5px;
  font-weight: 500;
  color: var(--text-muted);
}

.oobe-project-list {
  list-style: none;
  margin: 0;
  padding: 0;
  border-top: 1px solid var(--border-muted);
  border-bottom: 1px solid var(--border-muted);
  max-height: 196px;
  overflow-y: auto;
}

.oobe-project-list li + li {
  border-top: 1px solid var(--border-muted);
}

.oobe-project-row {
  width: 100%;
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 6px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  text-align: left;
  cursor: pointer;
  transition: background-color 120ms ease;
}

.oobe-project-row:hover {
  background: var(--surface-hover);
}

.oobe-project-row.is-selected {
  background: var(--surface-section);
}

.oobe-project-icon {
  flex-shrink: 0;
  color: var(--text-muted);
}

.oobe-project-row.is-selected .oobe-project-icon {
  color: var(--text-primary);
}

.oobe-project-copy {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.oobe-project-copy strong {
  font-size: 13px;
  font-weight: 500;
  color: var(--text-primary);
}

.oobe-project-copy span {
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 11px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.oobe-project-check {
  flex-shrink: 0;
  color: var(--text-primary);
}

.oobe-companion-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding-top: 14px;
  border-top: 1px solid var(--border-muted);
}

.oobe-companion-copy {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.oobe-companion-label {
  font-size: 13px;
  font-weight: 500;
  color: var(--text-primary);
}

.oobe-companion-desc {
  font-size: 12px;
  color: var(--text-secondary);
}
</style>
