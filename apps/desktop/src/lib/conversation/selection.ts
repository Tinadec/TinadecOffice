import { ref } from 'vue'

/**
 * Which sub-agent the right-column "Sub-agents" card shows. The in-feed card's "open in
 * panel" writes it; the panel card reads it. Window-scoped, like the feed it points into.
 */
export interface SubAgentSelection {
  runId: string
  taskId: string
}

export const conversationSelection = ref<SubAgentSelection | null>(null)

export function selectSubAgent(selection: SubAgentSelection | null): void {
  conversationSelection.value = selection
}
