<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Copy, FileCode2, FileText, Trash2 } from '@lucide/vue'
import { UiBadge, UiButton, UiSheet, UiSwitch, UiTextarea } from '@/components/ui'
import { api } from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import { toErrorState } from '@/composables/useErrorState'
import type { ToolSkillResource } from '@/settings/toolSettings'

const props = defineProps<{
  open: boolean
  skill?: ToolSkillResource | null
  projectId?: string
}>()

const emit = defineEmits<{
  close: []
  updated: [skill: ToolSkillResource]
  deleted: [resourceId: string]
}>()

const { t } = useI18n()
const { notify } = useNotifications()

const detail = ref<ToolSkillResource | null>(null)
const loadingDetail = ref(false)
const activeTab = ref<'preview' | 'edit'>('preview')
const selectedFilePath = ref('SKILL.md')
const subFileContent = ref('')
const loadingSubFile = ref(false)
const editorContent = ref('')
const saving = ref(false)
const toggling = ref(false)

const currentSkill = computed(() => detail.value ?? props.skill ?? null)
const isReadOnly = computed(() => {
  const s = currentSkill.value
  if (!s) return true
  return Boolean(s.read_only || s.scope === 'builtin' || s.source_kind === 'builtin')
})
const packageFiles = computed(() => currentSkill.value?.package_files ?? [])

async function loadSkillDetail(skill: ToolSkillResource) {
  loadingDetail.value = true
  selectedFilePath.value = 'SKILL.md'
  subFileContent.value = ''
  activeTab.value = 'preview'
  try {
    const loaded = await api.getToolSkill(skill.resource_id, props.projectId || undefined)
    detail.value = loaded
    editorContent.value = loaded.content ?? ''
  } catch (err) {
    detail.value = skill
    editorContent.value = skill.content ?? ''
    const errState = toErrorState(err, 'toolsSettings.skills')
    notify.error(errState.details || errState.message)
  } finally {
    loadingDetail.value = false
  }
}

watch(
  () => [props.open, props.skill?.resource_id] as const,
  ([isOpen, resourceId]) => {
    if (isOpen && props.skill && resourceId) {
      void loadSkillDetail(props.skill)
    } else if (!isOpen) {
      detail.value = null
      selectedFilePath.value = 'SKILL.md'
    }
  },
  { immediate: true },
)

async function openSubFile(path: string) {
  selectedFilePath.value = path
  if (path === 'SKILL.md' || !currentSkill.value) return
  loadingSubFile.value = true
  try {
    const file = await api.getToolSkillFile(currentSkill.value.resource_id, path, props.projectId || undefined)
    subFileContent.value = file.content ?? t('toolsSettings.binaryResource')
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.skills')
    subFileContent.value = errState.details || errState.message
  } finally {
    loadingSubFile.value = false
  }
}

async function onToggleEnabled(next: boolean) {
  if (!currentSkill.value) return
  toggling.value = true
  try {
    const updated = await api.setToolSkillEnabled(currentSkill.value.resource_id, next, props.projectId || undefined)
    detail.value = updated
    emit('updated', updated)
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.skills')
    notify.error(errState.details || errState.message)
  } finally {
    toggling.value = false
  }
}

async function onSaveEdit() {
  if (!currentSkill.value || isReadOnly.value) return
  saving.value = true
  try {
    const updated = await api.saveToolSkill(
      currentSkill.value.resource_id,
      {
        content: editorContent.value,
        expected_file_hash: currentSkill.value.file_hash ?? undefined,
      },
      currentSkill.value.revision ?? 0,
      props.projectId || undefined,
    )
    detail.value = updated
    editorContent.value = updated.content ?? editorContent.value
    activeTab.value = 'preview'
    notify.success(t('toolsSettings.saved'))
    emit('updated', updated)
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.skills')
    notify.error(errState.details || errState.message)
  } finally {
    saving.value = false
  }
}

async function onCopyToProject() {
  if (!currentSkill.value || !props.projectId) return
  try {
    const copied = await api.copyToolSkillToProject(currentSkill.value.resource_id, props.projectId)
    notify.success(t('toolsSettings.saved'))
    emit('updated', copied)
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.skills')
    notify.error(errState.details || errState.message)
  }
}

async function onDelete() {
  if (!currentSkill.value || isReadOnly.value) return
  if (!window.confirm(t('toolsSettings.deleteResource'))) return
  try {
    await api.deleteToolSkill(currentSkill.value.resource_id, currentSkill.value.revision ?? 0, props.projectId || undefined)
    emit('deleted', currentSkill.value.resource_id)
  } catch (err) {
    const errState = toErrorState(err, 'toolsSettings.skills')
    notify.error(errState.details || errState.message)
  }
}
</script>

<template>
  <UiSheet :open="open" side="right" @update:open="val => { if (!val) emit('close') }">
    <div v-if="currentSkill" class="tool-center-drawer tool-center-skill-drawer" data-testid="skill-detail-drawer">
      <div class="tool-center-drawer-head">
        <div>
          <h3>{{ currentSkill.name }}</h3>
          <p class="setting-desc">{{ currentSkill.description }}</p>
          <div class="tool-center-badge-row">
            <UiBadge variant="outline">{{ currentSkill.scope || currentSkill.source_kind || 'user' }}</UiBadge>
            <UiBadge v-if="currentSkill.version" variant="outline">v{{ currentSkill.version }}</UiBadge>
            <UiBadge v-if="isReadOnly" variant="outline">{{ t('toolsSettings.builtinBadge') }}</UiBadge>
          </div>
        </div>
        <div class="tool-center-drawer-actions">
          <UiSwitch
            :model-value="currentSkill.enabled"
            :disabled="toggling"
            :aria-label="currentSkill.name"
            @update:model-value="val => onToggleEnabled(Boolean(val))"
          />
          <UiButton
            v-if="projectId && isReadOnly"
            size="sm"
            variant="outline"
            @click="onCopyToProject"
          >
            <Copy :size="14" />
            {{ t('toolsSettings.copyToProject') }}
          </UiButton>
          <UiButton
            v-if="!isReadOnly"
            size="sm"
            variant="ghost"
            :aria-label="t('toolsSettings.delete')"
            @click="onDelete"
          >
            <Trash2 :size="14" />
          </UiButton>
        </div>
      </div>

      <p v-if="isReadOnly" class="tool-center-hint">
        {{ t('toolsSettings.center.skills.readOnlyHint') }}
      </p>

      <div class="tool-center-skill-split">
        <aside class="tool-center-subfiles-nav">
          <strong class="tool-center-subfiles-heading">{{ t('toolsSettings.center.skills.subFilesTitle') }}</strong>
          <button
            type="button"
            class="tool-center-subfile-item"
            :class="{ active: selectedFilePath === 'SKILL.md' }"
            @click="openSubFile('SKILL.md')"
          >
            <FileText :size="14" />
            <span>{{ t('toolsSettings.center.skills.skillMdTitle') }}</span>
          </button>
          <button
            v-for="file in packageFiles"
            :key="file.path"
            type="button"
            class="tool-center-subfile-item"
            :class="{ active: selectedFilePath === file.path }"
            @click="openSubFile(file.path)"
          >
            <FileCode2 :size="14" />
            <span>{{ file.path }}</span>
          </button>
          <p v-if="!packageFiles.length" class="tool-center-hint">
            {{ t('toolsSettings.center.skills.noSubFiles') }}
          </p>
        </aside>

        <section class="tool-center-skill-content">
          <template v-if="selectedFilePath === 'SKILL.md'">
            <div v-if="!isReadOnly" class="tool-center-segmented">
              <button
                type="button"
                class="tool-center-seg-btn"
                :class="{ active: activeTab === 'preview' }"
                @click="activeTab = 'preview'"
              >
                {{ t('toolsSettings.center.skills.previewTab') }}
              </button>
              <button
                type="button"
                class="tool-center-seg-btn"
                :class="{ active: activeTab === 'edit' }"
                @click="activeTab = 'edit'"
              >
                {{ t('toolsSettings.center.skills.editTab') }}
              </button>
            </div>

            <div v-if="loadingDetail" class="tool-center-empty">
              {{ t('common.loading') }}
            </div>
            <pre v-else-if="activeTab === 'preview' || isReadOnly" class="tool-center-skill-pre">{{ currentSkill.content || currentSkill.description }}</pre>
            <div v-else class="tool-center-skill-editor-wrap">
              <UiTextarea v-model="editorContent" :rows="16" class="tool-center-mono-textarea" />
              <div class="tool-center-modal-footer">
                <UiButton :disabled="saving" @click="onSaveEdit">
                  {{ saving ? t('toolsSettings.saving') : t('settings.save') }}
                </UiButton>
              </div>
            </div>
          </template>

          <template v-else>
            <div v-if="loadingSubFile" class="tool-center-empty">
              {{ t('common.loading') }}
            </div>
            <pre v-else class="tool-center-skill-pre">{{ subFileContent }}</pre>
          </template>
        </section>
      </div>
    </div>
  </UiSheet>
</template>
