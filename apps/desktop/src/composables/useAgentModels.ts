import { computed, ref, watch, type Ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useNotifications } from '@/composables/useNotifications'
import { api, type AgentDirectoryItemDto } from '@/api'
import { selectedStorage } from '@/lib/storageScope'

/** A provider instance and model; `model` is null for runtimes that pick their own (CLI/ACP). */
export interface ModelChoice {
  provider_instance_id: string
  model: string | null
}

/**
 * Each agent's own model, as the composer reads and sets it. It is the per-agent runtime binding
 * the agent center writes, so the two places edit one setting. The conversation agent — the one
 * that answers the user, frozen on the session when it was created — is the composer's main model;
 * the other agents are listed apart.
 *
 * An agent without a fixed model follows the default: it runs on the default model (the model
 * center's chat route) at the root of a run, and on the model of the agent that spawned it
 * otherwise. Requests run in the current storage scope, like the rest of the composer.
 */
export function useAgentModels(options: { conversationSlug: Ref<string>; modeVersionId: Ref<string | null | undefined> }) {
  const { t } = useI18n()
  const { notify } = useNotifications()
  const agents = ref<AgentDirectoryItemDto[]>([])
  /** False until a read succeeds; the composer falls back to the default model until then. */
  const loaded = ref(false)
  const busy = ref(false)
  let read = 0

  const conversationAgent = computed(() => agents.value.find(agent => agent.slug === options.conversationSlug.value) ?? null)
  /** Enabled agents other than the conversation agent; the ones the selected mode uses first. */
  const otherAgents = computed(() => agents.value
    .filter(agent => agent.enabled && agent.id !== conversationAgent.value?.id)
    .sort((a, b) => Number(Boolean(usageInMode(b))) - Number(Boolean(usageInMode(a)))))

  function usageInMode(agent: AgentDirectoryItemDto) {
    const version = options.modeVersionId.value
    return version ? agent.mode_usages?.find(usage => usage.mode_version_id === version) ?? null : null
  }

  /** The model this agent was given here or in the agent center; null when it follows the default. */
  function fixedChoice(agent: AgentDirectoryItemDto): ModelChoice | null {
    const binding = agent.model_binding
    return binding?.mode === 'fixed' && binding.provider_instance_id
      ? { provider_instance_id: binding.provider_instance_id, model: binding.model ?? null }
      : null
  }

  /**
   * The model the agent resolves to in the selected mode: its own fixed model, else what Core
   * previews for it there (a mode can set a node's model). Null when nothing names one, which is
   * the default model at the root and the spawning agent's model below it.
   */
  function effectiveChoice(agent: AgentDirectoryItemDto): ModelChoice | null {
    const fixed = fixedChoice(agent)
    if (fixed) return fixed
    const usage = usageInMode(agent)
    const selection = usage ? agent.effective_previews?.[`${usage.mode_slug}:${usage.node_key}`]?.expected_selection : null
    return selection?.provider_instance_id && selection.available !== false
      ? { provider_instance_id: selection.provider_instance_id, model: selection.model ?? null }
      : null
  }

  async function load() {
    const sequence = ++read
    try {
      const rows = await api.listAgents()
      if (sequence !== read) return
      agents.value = Array.isArray(rows) ? rows : []
      loaded.value = true
    } catch { /* keeps what was last known */ }
  }

  /**
   * Give an agent a model, or (null) let it follow the default. The binding's tool-scope override
   * is sent back as it was: the endpoint replaces the whole binding, and leaving it out would clear
   * a tool scope set elsewhere.
   */
  async function setModel(agent: AgentDirectoryItemDto, choice: ModelChoice | null) {
    if (busy.value || !loaded.value) return
    const toolScope = agent.model_binding?.tool_scope_override
    busy.value = true
    try {
      await api.putAgentRuntimeBinding(agent.id, {
        mode: choice ? 'fixed' : 'inherit',
        provider_instance_id: choice?.provider_instance_id ?? null,
        model: choice?.model ?? null,
        ...(Array.isArray(toolScope) ? { tool_scope: toolScope } : {}),
      })
    } catch (error) {
      notify.error(error, { title: t('composer.agentModels.failed', { agent: agent.display_name }) })
    } finally {
      await load()
      busy.value = false
    }
  }

  watch(selectedStorage, () => { loaded.value = false; void load() })
  return { agents, loaded, busy, load, conversationAgent, otherAgents, usageInMode, fixedChoice, effectiveChoice, setModel }
}

export type AgentModels = ReturnType<typeof useAgentModels>
