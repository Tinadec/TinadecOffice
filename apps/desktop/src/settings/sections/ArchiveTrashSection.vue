<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArchiveRestore, FolderOpen, MessageSquare, Trash2 } from '@lucide/vue'
import { generatedApi, type ProjectDto, type SessionDto } from '@/generated/client'
import { UiButton } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'

const { t } = useI18n()
const { confirm, notify } = useNotifications()

const loading = ref(false)
const archivedProjects = ref<ProjectDto[]>([])
const archivedSessions = ref<SessionDto[]>([])
const trashedProjects = ref<ProjectDto[]>([])
const trashedSessions = ref<SessionDto[]>([])
const projectNames = ref<Map<string, string>>(new Map())

async function load() {
  loading.value = true
  try {
    const [activeProjects, archivedP, trashedP, archivedS, trashedS] = await Promise.all([
      generatedApi.listProjects('active'),
      generatedApi.listProjects('archived'),
      generatedApi.listProjects('trashed'),
      generatedApi.listSessions(undefined, 'archived'),
      generatedApi.listSessions(undefined, 'trashed'),
    ])
    archivedProjects.value = archivedP
    trashedProjects.value = trashedP
    archivedSessions.value = archivedS
    trashedSessions.value = trashedS
    const names = new Map<string, string>()
    for (const project of [...activeProjects, ...archivedP, ...trashedP]) names.set(project.id, project.name)
    projectNames.value = names
  } catch (err) {
    notify.error(err, { title: t('settings.loadArchiveFailed') })
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  void load()
})

const archivedEmpty = computed(() => archivedProjects.value.length === 0 && archivedSessions.value.length === 0)
const trashEmpty = computed(() => trashedProjects.value.length === 0 && trashedSessions.value.length === 0)

function projectNameOf(session: SessionDto): string {
  return projectNames.value.get(session.project_id) ?? session.project_id
}

async function restoreProject(project: ProjectDto) {
  try {
    await generatedApi.restoreProject(project.id)
    await load()
  } catch (err) {
    notify.error(err, { title: t('settings.loadArchiveFailed') })
  }
}

async function restoreSession(session: SessionDto) {
  try {
    await generatedApi.restoreSession(session.id)
    await load()
  } catch (err) {
    notify.error(err, { title: t('settings.loadArchiveFailed') })
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
    await generatedApi.purgeProject(project.id)
    await load()
  } catch (err) {
    notify.error(err, { title: t('settings.deletePermanently') })
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
    await generatedApi.purgeSession(session.id)
    await load()
  } catch (err) {
    notify.error(err, { title: t('settings.deletePermanently') })
  }
}
</script>

<template>
  <div class="archive-trash-section">
    <h2>{{ t('settings.archiveTrash') }}</h2>
    <p class="archive-trash-subtitle">{{ t('settings.archiveTrashSubtitle') }}</p>

    <section class="archive-trash-group">
      <h3><ArchiveRestore :size="14" /> {{ t('settings.archivedGroup') }}</h3>
      <p v-if="archivedEmpty" class="archive-trash-empty">{{ t('settings.emptyArchived') }}</p>
      <template v-else>
        <div v-if="archivedProjects.length" class="archive-trash-list-label">{{ t('settings.projectsGroup') }}</div>
        <div
          v-for="project in archivedProjects"
          :key="project.id"
          class="archive-trash-row"
          data-testid="archived-project-row"
        >
          <FolderOpen :size="14" class="archive-trash-row-icon" />
          <span class="archive-trash-row-title">{{ project.name }}</span>
          <UiButton variant="outline" size="sm" @click="restoreProject(project)">{{ t('settings.restore') }}</UiButton>
        </div>
        <div v-if="archivedSessions.length" class="archive-trash-list-label">{{ t('settings.sessionsGroup') }}</div>
        <div
          v-for="session in archivedSessions"
          :key="session.id"
          class="archive-trash-row"
          data-testid="archived-session-row"
        >
          <MessageSquare :size="14" class="archive-trash-row-icon" />
          <span class="archive-trash-row-title">
            {{ session.title }}
            <span class="archive-trash-row-meta">{{ t('settings.sessionInProject', { name: projectNameOf(session) }) }}</span>
          </span>
          <UiButton variant="outline" size="sm" @click="restoreSession(session)">{{ t('settings.restore') }}</UiButton>
        </div>
      </template>
    </section>

    <section class="archive-trash-group">
      <h3><Trash2 :size="14" /> {{ t('settings.trashGroup') }}</h3>
      <p v-if="trashEmpty" class="archive-trash-empty">{{ t('settings.emptyTrash') }}</p>
      <template v-else>
        <div v-if="trashedProjects.length" class="archive-trash-list-label">{{ t('settings.projectsGroup') }}</div>
        <div
          v-for="project in trashedProjects"
          :key="project.id"
          class="archive-trash-row"
          data-testid="trashed-project-row"
        >
          <FolderOpen :size="14" class="archive-trash-row-icon" />
          <span class="archive-trash-row-title">{{ project.name }}</span>
          <UiButton variant="outline" size="sm" @click="restoreProject(project)">{{ t('settings.restore') }}</UiButton>
          <UiButton variant="destructive" size="sm" @click="purgeProject(project)">{{ t('settings.deletePermanently') }}</UiButton>
        </div>
        <div v-if="trashedSessions.length" class="archive-trash-list-label">{{ t('settings.sessionsGroup') }}</div>
        <div
          v-for="session in trashedSessions"
          :key="session.id"
          class="archive-trash-row"
          data-testid="trashed-session-row"
        >
          <MessageSquare :size="14" class="archive-trash-row-icon" />
          <span class="archive-trash-row-title">
            {{ session.title }}
            <span class="archive-trash-row-meta">{{ t('settings.sessionInProject', { name: projectNameOf(session) }) }}</span>
          </span>
          <UiButton variant="outline" size="sm" @click="restoreSession(session)">{{ t('settings.restore') }}</UiButton>
          <UiButton variant="destructive" size="sm" @click="purgeSession(session)">{{ t('settings.deletePermanently') }}</UiButton>
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

.archive-trash-subtitle {
  margin: 0;
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
