import { ref, computed } from 'vue'
import { defineStore } from 'pinia'
import { generatedApi, type SessionDto, type MessageDto } from '@/generated/client'

export const useSessionStore = defineStore('session', () => {
  const sessions = ref<SessionDto[]>([])
  const messages = ref<MessageDto[]>([])
  const selectedId = ref<string | null>(null)
  const loading = ref(false)
  const error = ref<string | null>(null)

  const current = computed(() => sessions.value.find(s => s.id === selectedId.value) ?? null)

  async function fetchForProject(projectId: string) {
    loading.value = true; error.value = null
    try {
      const all = await generatedApi.listSessions(projectId)
      // replace only this project's sessions
      const others = sessions.value.filter(s => s.project_id !== projectId)
      sessions.value = [...all, ...others]
      if (!selectedId.value || !sessions.value.find(s => s.id === selectedId.value)) {
        selectedId.value = all[0]?.id ?? null
      }
    } catch (e) { error.value = e instanceof Error ? e.message : String(e) }
    finally { loading.value = false }
  }

  async function create(projectId: string, title?: string) {
    const s = await generatedApi.createSession(projectId, title)
    sessions.value = [s, ...sessions.value]
    selectedId.value = s.id
    return s
  }

  async function fetchMessages(sessionId: string) {
    messages.value = await generatedApi.listMessages(sessionId)
  }

  function select(id: string) { selectedId.value = id }

  return { sessions, messages, selectedId, current, loading, error, fetchForProject, create, fetchMessages, select }
})
