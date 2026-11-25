<script lang="ts">
import type { OrchestrationFocusTarget } from '@/components/OrchestrationTab.vue'

const FOCUS_EVENT_NAME = 'tinadec:focus-orchestration-node'
const PENDING_TTL_MS = 5000

let pendingFocus: { payload: OrchestrationFocusTarget; at: number } | null = null
let globalListenerInstalled = false
let focusSeq = 0

function normalizeFocusDetail(detail: unknown): OrchestrationFocusTarget | null {
  if (!detail) return null
  if (typeof detail === 'string') {
    const trimmed = detail.trim()
    return trimmed ? { target: trimmed, nonce: ++focusSeq } : null
  }
  if (typeof detail === 'object') {
    const raw = detail as Record<string, unknown>
    const str = (key: string) => (typeof raw[key] === 'string' && raw[key]!.trim() ? raw[key]!.trim() : null)
    const payload: OrchestrationFocusTarget = {
      target: str('target'),
      taskId: str('taskId') ?? str('task_id'),
      taskKey: str('taskKey') ?? str('task_key'),
      handle: str('handle') ?? str('workerHandle') ?? str('worker_handle'),
      instanceId: str('instanceId') ?? str('instance_id'),
      agentSlug: str('agentSlug') ?? str('agent_slug'),
      nonce: ++focusSeq,
    }
    if (
      payload.target
      || payload.taskId
      || payload.taskKey
      || payload.handle
      || payload.instanceId
      || payload.agentSlug
    ) {
      return payload
    }
  }
  return null
}

function ensureGlobalFocusCapture(): void {
  if (globalListenerInstalled || typeof window === 'undefined') return
  globalListenerInstalled = true
  window.addEventListener(FOCUS_EVENT_NAME, ((event: Event) => {
    const parsed = normalizeFocusDetail((event as CustomEvent).detail)
    if (parsed) pendingFocus = { payload: parsed, at: Date.now() }
  }) as EventListener)
}

ensureGlobalFocusCapture()
</script>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import OrchestrationTab from '@/components/OrchestrationTab.vue'
import CardScroll from './CardScroll.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController
const focusedTarget = ref<OrchestrationFocusTarget | null>(null)

function onFocusEvent(event: Event): void {
  const parsed = normalizeFocusDetail((event as CustomEvent).detail)
  if (!parsed) return
  pendingFocus = null
  focusedTarget.value = parsed
}

onMounted(() => {
  if (pendingFocus && Date.now() - pendingFocus.at <= PENDING_TTL_MS) {
    focusedTarget.value = pendingFocus.payload
    pendingFocus = null
  }
  if (typeof window !== 'undefined') {
    window.addEventListener(FOCUS_EVENT_NAME, onFocusEvent as EventListener)
  }
})

onBeforeUnmount(() => {
  if (typeof window !== 'undefined') {
    window.removeEventListener(FOCUS_EVENT_NAME, onFocusEvent as EventListener)
  }
})
</script>

<template>
  <CardScroll>
    <OrchestrationTab
      :snapshot="c.orchestration.value"
      :tool-executions="c.toolExecutions.value"
      :session-id="c.selectedSessionId.value"
      :runs="c.runs.value"
      :active-run-id="c.stoppableRunId?.value ?? null"
      :focused-target="focusedTarget"
      @execute-tool="c.executeCatalogTool($event)"
    />
  </CardScroll>
</template>
