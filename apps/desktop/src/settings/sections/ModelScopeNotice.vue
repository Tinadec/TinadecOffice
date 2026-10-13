<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { UiButton } from '@/components/ui'
import { api } from '@/api'
import { selectedStorage } from '@/lib/storageScope'
import { useNotifications } from '@/composables/useNotifications'

/**
 * The model center edits the user scope's models.toml. A workspace either runs on that same
 * file (new workspaces do) or keeps its own copy (workspaces created before, or a project that
 * arrived with its own .tinadec). Before this said so, a model set up here looked "unusable"
 * inside a workspace on its own copy, and one deleted here stayed in use there. While a
 * workspace is open this names which one its conversations use and switches between them;
 * the workspace's own file is never deleted, so switching back restores it.
 */
const { t } = useI18n()
const { notify, confirm } = useNotifications()

const scopeId = computed(() => selectedStorage.value)
const scopeName = ref('')
const follows = ref<boolean | null>(null)
/** Whether a workspace's own copy still matches the user file; null when unknown or moot. */
const differs = ref<boolean | null>(null)
const busy = ref(false)
let read = 0

async function inspect() {
  const current = ++read
  follows.value = null
  differs.value = null
  if (scopeId.value === 'user') return
  const storageId = scopeId.value
  try {
    const scope = (await api.listStorageScopes()).find(row => row.storage_id === storageId)
    if (current !== read || !scope) return
    scopeName.value = scope.workspace?.name || scope.project_root?.split(/[\\/]/).filter(Boolean).at(-1) || storageId
    follows.value = scope.follow_user_models === true
    if (follows.value) return
    const [user, workspace] = await Promise.all([
      api.getConfigurationDocument('user', 'models'),
      api.getConfigurationDocument(storageId, 'models'),
    ])
    if (current === read) differs.value = user.content_hash !== workspace.content_hash
  } catch {
    // Unknown, not "in sync": no verdict and no switch offered on a guess.
    if (current === read) differs.value = null
  }
}

async function setFollow(followUser: boolean) {
  const storageId = scopeId.value
  if (storageId === 'user' || busy.value) return
  if (followUser && !await confirm({
    title: t('settings.modelScope.followTitle', { name: scopeName.value }),
    message: t('settings.modelScope.followMessage', { name: scopeName.value }),
    confirmLabel: t('settings.modelScope.followConfirm'),
    cancelLabel: t('settings.cancel'),
  })) return
  busy.value = true
  try {
    await api.setWorkspaceModelSource(storageId, followUser)
    notify.success(t(followUser ? 'settings.modelScope.followed' : 'settings.modelScope.detached', { name: scopeName.value }))
  } catch (error) {
    notify.error(error, { title: t('settings.modelScope.switchFailed') })
  } finally {
    busy.value = false
    await inspect()
  }
}

onMounted(() => { void inspect() })
watch(scopeId, () => { void inspect() })
defineExpose({ inspect })
</script>

<template>
  <div v-if="scopeId !== 'user' && follows !== null" class="model-scope-notice" role="status" data-testid="model-scope-notice" :data-follows="follows ? 'true' : 'false'">
    <p v-if="follows">{{ t('settings.modelScope.follows', { name: scopeName }) }}</p>
    <p v-else>
      {{ t('settings.modelScope.ownCopy', { name: scopeName }) }}
      <strong v-if="differs === true" data-testid="model-scope-differs">{{ t('settings.modelScope.differs') }}</strong>
      <span v-else-if="differs === false" data-testid="model-scope-same">{{ t('settings.modelScope.same') }}</span>
    </p>
    <UiButton
      v-if="follows"
      size="sm"
      variant="ghost"
      :disabled="busy"
      data-testid="model-scope-detach"
      @click="setFollow(false)"
    >{{ t('settings.modelScope.detach') }}</UiButton>
    <UiButton
      v-else
      size="sm"
      variant="outline"
      :disabled="busy"
      data-testid="model-scope-follow"
      @click="setFollow(true)"
    >{{ t('settings.modelScope.follow') }}</UiButton>
  </div>
</template>

<style scoped>
.model-scope-notice { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin: 0 0 12px; padding: 8px 12px; border: 1px solid var(--border-card); border-radius: 10px; background: var(--surface-section); font-size: 12px; color: var(--text-secondary); }
.model-scope-notice p { margin: 0; }
.model-scope-notice strong { margin-left: 4px; color: var(--accent-warning); font-weight: 600; }
.model-scope-notice span { margin-left: 4px; }
</style>
