import { ref, computed } from 'vue'
import { defineStore } from 'pinia'
import { generatedApi, type ProjectDto } from '@/generated/client'

export const useProjectStore = defineStore('project', () => {
  const projects = ref<ProjectDto[]>([])
  const selectedId = ref<string | null>(null)
  const loading = ref(false)
  const error = ref<string | null>(null)

  const current = computed(() => projects.value.find(p => p.id === selectedId.value) ?? null)

  async function fetchAll() {
    loading.value = true; error.value = null
    try { projects.value = await generatedApi.listProjects(); if (!selectedId.value) selectedId.value = projects.value[0]?.id ?? null } catch (e) { error.value = e instanceof Error ? e.message : String(e) }
    finally { loading.value = false }
  }
  async function create(name: string, path: string) {
    const p = await generatedApi.createProject(name, path)
    projects.value = [p, ...projects.value.filter(x => x.id !== p.id)]
    selectedId.value = p.id
    return p
  }
  function select(id: string) { selectedId.value = id }

  return { projects, selectedId, current, loading, error, fetchAll, create, select }
})
