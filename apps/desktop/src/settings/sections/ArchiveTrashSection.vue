<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArchiveRestore, FolderOpen, MessageSquare, RefreshCw, Trash2 } from '@lucide/vue'
import { generatedApi, type ProjectDto } from '@/generated/client'
import type { SessionDto } from '@/api'
import { UiButton } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import { selectionKey } from '@/lib/storageScope'
import { loadSessionCatalog } from '@/lib/sessionRoster'
import { toErrorState } from '@/composables/useErrorState'
import { useHostAccess } from '@/lib/hostAccess'
import { isAbortError } from '@/lib/isAbortError'

const ARCHIVE_TRASH_LOAD_KEY = 'archive-trash-load'

const { t } = useI18n()
const { confirm, notify, dismissByKey } = useNotifications()

const { canAccessBackend } = useHostAccess()
const loading = ref(false)
let generation = 0
let controller: AbortController | null = null
const activeProjects = ref<ProjectDto[]>([])
const archivedProjects = ref<ProjectDto[]>([])
const archivedSessions = ref<SessionDto[]>([])
const trashedProjects = ref<ProjectDto[]>([])
const trashedSessions = ref<SessionDto[]>([])
const projectNames = ref<Map<string, string>>(new Map())

async function load() {
  if (!canAccessBackend.value) return
  const read = ++generation
  controller?.abort()
  const request = new AbortController()
  controller = request
  loading.value = true
  const rawErrors: unknown[] = []
  const scopeWarnings: string[] = []
  const report = (error: unknown) => {
    if (read !== generation || request.signal.aborted || isAbortError(error)) return
    rawErrors.push(error)
  }
  const lists = [activeProjects, archivedProjects, trashedProjects]
  try {
    await Promise.allSettled([
      ...(['active', 'archived', 'trashed'] as const).map((lifecycle, index) =>
        generatedApi.listProjects(lifecycle, { signal: request.signal }).then((rows) => {
          if (read === generation && !request.signal.aborted) lists[index]!.value = rows
        }).catch(report),
      ),
      ...(['archived', 'trashed'] as const).map((lifecycle) => {
        const target = lifecycle === 'archived' ? archivedSessions : trashedSessions
        return loadSessionCatalog({ lifecycleStatus: lifecycle, previous: target.value, signal: request.signal }).then((result) => {
          if (read !== generation || request.signal.aborted) return
          target.value = result.rows
          for (const failure of result.failures) {
            scopeWarnings.push(
              failure.source.storageId + ': ' + failure.error.message + (failure.error.traceId ? ' · trace_id: ' + failure.error.traceId : ''),
            )
          }
        }).catch(report)
      }),
    ])
    if (read !== generation || request.signal.aborted) return
    const names = new Map<string, string>()
    for (const project of [...activeProjects.value, ...archivedProjects.value, ...trashedProjects.value]) {
      names.set(selectionKey({ ...project, storage_id: project.storage_id ?? 'user' }), project.name)
    }
    projectNames.value = names

    if (rawErrors.length > 0) {
      const extraDetails = [
        ...rawErrors.slice(1).map((err) => {
          const failure = toErrorState(err, t('settings.loadArchiveFailed'))
          return [failure.message, failure.details, failure.traceId ? `trace_id: ${failure.traceId}` : ''].filter(Boolean).join('\n')
        }),
        ...scopeWarnings,
      ].filter(Boolean).join('\n')
      notify.error(rawErrors[0], {
        key: ARCHIVE_TRASH_LOAD_KEY,
        title: t('settings.loadArchiveFailed'),
        ...(extraDetails ? { details: extraDetails } : {}),
        action: { label: t('settings.retry'), run: () => load() },
      })
    } else if (scopeWarnings.length > 0) {
      notify.warning({
        key: ARCHIVE_TRASH_LOAD_KEY,
        title: t('settings.loadArchiveFailed'),
        message: scopeWarnings[0]!,
        ...(scopeWarnings.length > 1 ? { details: scopeWarnings.slice(1).join('\n') } : {}),
        action: { label: t('settings.retry'), run: () => load() },
      })
    } else {
      dismissByKey(ARCHIVE_TRASH_LOAD_KEY)
    }
  } finally {
    if (read === generation) loading.value = false
  }
}

watch(canAccessBackend, (ready, wasReady) => {
  if (ready) {
    if (!wasReady) dismissByKey(ARCHIVE_TRASH_LOAD_KEY)
    void load()
  } else {
    ++generation
    controller?.abort()
    loading.value = false
  }
})
onMounted(() => { void load() })
onBeforeUnmount(() => {
  ++generation
  controller?.abort()
  dismissByKey(ARCHIVE_TRASH_LOAD_KEY)
})

const archivedEmpty = computed(() => archivedProjects.value.length === 0 && archivedSessions.value.length === 0)
const trashEmpty = computed(() => trashedProjects.value.length === 0 && trashedSessions.value.length === 0)

function projectNameOf(session: SessionDto): string {
  if (!session.project_id) return t('sidebar.freeConversations')
  return projectNames.value.get(selectionKey({ id: session.project_id, storage_id: session.storage_id })) ?? session.project_id
}

async function restoreProject(project: ProjectDto) {
  try {
    await generatedApi.restoreProject(selectionKey(project))
    await load()
  } catch (err) {
    notify.error(err, { key: ARCHIVE_TRASH_LOAD_KEY, title: t('settings.loadArchiveFailed') })
  }
}

async function restoreSession(session: SessionDto) {
  try {
    await generatedApi.restoreSession(selectionKey(session))
    await load()
  } catch (err) {
    notify.error(err, { key: ARCHIVE_TRASH_LOAD_KEY, title: t('settings.loadArchiveFailed') })
  }
}

async function purgeProject(project: ProjectDto) {
  const ok = await confirm({
    title: t('settings.purgeConfirmTitle'),
    message: t('settings.purgeProjectConfirmMessage', { name: project.name }),
    confirmLabel: t('settings.deletePermanently'),
    destructive: true,
  })
  if (!ok) return
  try {
    await generatedApi.purgeProject(selectionKey(project))
    await load()
  } catch (err) {
    notify.error(err, { key: ARCHIVE_TRASH_LOAD_KEY, title: t('settings.deletePermanently') })
  }
}

async function purgeSession(session: SessionDto) {
  const ok = await confirm({
    title: t('settings.purgeConfirmTitle'),
    message: t('settings.purgeSessionConfirmMessage', { name: session.title || session.id }),
    confirmLabel: t('settings.deletePermanently'),
    destructive: true,
  })
  if (!ok) return
  try {
    await generatedApi.purgeSession(selectionKey(session))
    await load()
  } catch (err) {
    notify.error(err, { key: ARCHIVE_TRASH_LOAD_KEY, title: t('settings.deletePermanently') })
  }
}
</script>

<template>
  <div class="archive-trash-section">
    <div class="archive-trash-head">
      <div>
        <h2>{{ t('settings.archiveTrash') }}</h2>
        <p class="archive-trash-subtitle">{{ t('settings.archiveTrashSubtitle') }}</p>
      </div>
      <UiButton
        variant="ghost"
        size="sm"
        :disabled="loading || !canAccessBackend"
        :aria-label="t('common.refresh')"
        @click="load"
      >
        <RefreshCw :size="14" />
      </UiButton>
    </div>

    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <section class="archive-trash-group">
      <h3><ArchiveRestore :size="14" /> {{ t('settings.archivedGroup') }}</h3>
      <p v-if="!loading && archivedEmpty" class="archive-trash-empty">{{ t('settings.emptyArchived') }}</p>
      <template v-else>
        <div v-if="archivedProjects.length" class="archive-trash-list-label">{{ t('settings.projectsGroup') }}</div>
        <div
          v-for="project in archivedProjects"
          :key="selectionKey(project)"
          class="archive-trash-row"
          data-testid="archived-project-row"
        >
          <FolderOpen :size="14" class="archive-trash-row-icon" />
          <span class="archive-trash-row-title">{{ project.name }}</span>
          <UiButton variant="outline" size="sm" :disabled="!canAccessBackend || loading" @click="restoreProject(project)">{{ t('settings.restore') }}</UiButton>
        </div>
        <div v-if="archivedSessions.length" class="archive-trash-list-label">{{ t('settings.sessionsGroup') }}</div>
        <div
          v-for="session in archivedSessions"
          :key="selectionKey(session)"
          class="archive-trash-row"
          data-testid="archived-session-row"
        >
          <MessageSquare :size="14" class="archive-trash-row-icon" />
          <span class="archive-trash-row-title">
            {{ session.title }}
            <span class="archive-trash-row-meta">{{ t('settings.sessionInProject', { name: projectNameOf(session) }) }}</span>
          </span>
          <UiButton variant="outline" size="sm" :disabled="!canAccessBackend || loading" @click="restoreSession(session)">{{ t('settings.restore') }}</UiButton>
        </div>
      </template>
    </section>

    <section class="archive-trash-group">
      <h3><Trash2 :size="14" /> {{ t('settings.trashGroup') }}</h3>
      <p v-if="!loading && trashEmpty" class="archive-trash-empty">{{ t('settings.emptyTrash') }}</p>
      <template v-else>
        <div v-if="trashedProjects.length" class="archive-trash-list-label">{{ t('settings.projectsGroup') }}</div>
        <div
          v-for="project in trashedProjects"
          :key="selectionKey(project)"
          class="archive-trash-row"
          data-testid="trashed-project-row"
        >
          <FolderOpen :size="14" class="archive-trash-row-icon" />
          <span class="archive-trash-row-title">{{ project.name }}</span>
          <UiButton variant="outline" size="sm" :disabled="!canAccessBackend || loading" @click="restoreProject(project)">{{ t('settings.restore') }}</UiButton>
          <UiButton variant="destructive" size="sm" :disabled="!canAccessBackend || loading" @click="purgeProject(project)">{{ t('settings.deletePermanently') }}</UiButton>
        </div>
        <div v-if="trashedSessions.length" class="archive-trash-list-label">{{ t('settings.sessionsGroup') }}</div>
        <div
          v-for="session in trashedSessions"
          :key="selectionKey(session)"
          class="archive-trash-row"
          data-testid="trashed-session-row"
        >
          <MessageSquare :size="14" class="archive-trash-row-icon" />
          <span class="archive-trash-row-title">
            {{ session.title }}
            <span class="archive-trash-row-meta">{{ t('settings.sessionInProject', { name: projectNameOf(session) }) }}</span>
          </span>
          <UiButton variant="outline" size="sm" :disabled="!canAccessBackend || loading" @click="restoreSession(session)">{{ t('settings.restore') }}</UiButton>
          <UiButton variant="destructive" size="sm" :disabled="!canAccessBackend || loading" @click="purgeSession(session)">{{ t('settings.deletePermanently') }}</UiButton>
        </div>
      </template>
    </section>
  </div>
</template>

<style scoped>
.archive-trash-section {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.archive-trash-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.archive-trash-head h2 {
  margin: 0;
}

.archive-trash-subtitle {
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--text-muted);
}

.archive-trash-group {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 12px;
  border-radius: 10px;
  background: var(--surface-section, var(--surface-raised));
}

.archive-trash-group h3 {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0;
  font-size: 13px;
  color: var(--text-primary);
}

.archive-trash-empty {
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--text-muted);
}

.archive-trash-list-label {
  margin-top: 6px;
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--text-muted);
}

.archive-trash-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 8px;
  border-radius: 8px;
  background: var(--surface-chrome, transparent);
}

.archive-trash-row-icon {
  flex: none;
  color: var(--text-muted);
}

.archive-trash-row-title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  color: var(--text-primary);
}

.archive-trash-row-meta {
  margin-left: 8px;
  font-size: 11px;
  color: var(--text-muted);
}
</style>
