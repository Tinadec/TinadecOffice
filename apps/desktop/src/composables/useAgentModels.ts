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

/** A model strategy that names something: a fixed model or a route purpose. */
export type NamedStrategy = { kind: 'fixed'; choice: ModelChoice } | { kind: 'route'; purpose: string }

type StrategyLike = { kind?: string; provider_instance_id?: string | null; model?: string | null; route_purpose?: string | null } | null | undefined

function named(strategy: StrategyLike): NamedStrategy | null {
  if (strategy?.kind === 'fixed' && strategy.provider_instance_id)
    return { kind: 'fixed', choice: { provider_instance_id: strategy.provider_instance_id, model: strategy.model ?? null } }
  if (strategy?.kind === 'route' && strategy.route_purpose) return { kind: 'route', purpose: strategy.route_purpose }
  return null
}

/**
 * Each agent's own model, as the composer reads and sets it. It is the per-agent runtime binding
 * the agent center writes, so the two places edit one setting. The conversation agent — the one
 * that answers the user, frozen on the session when it was created — is the composer's main model;
 * the other agents are listed apart.
 *
 * Without a model of its own an agent runs on what it is configured with: its node in the mode,
 * else its definition. Where that names nothing (every shipped agent), it follows the default — the
 * default model (the model center's chat route) at the root of a run, the model of the agent that
 * spawned it otherwise. Requests run in the current storage scope, like the rest of the composer.
 */
export function useAgentModels(options: { conversationSlug: Ref<string>; modeVersionId: Ref<string | null | undefined> }) {
  const { t } = useI18n()
  const { notify } = useNotifications()
  const agents = ref<AgentDirectoryItemDto[]>([])
  /** False until a read succeeds; the composer falls back to the default model until then. */
  const loaded = ref(false)
  /** The latest read failed and nothing has been read since the scope was entered. */
  const error = ref(false)
  const busy = ref(false)
  let read = 0
  let applied = 0
  let epoch = 0

  const conversationAgent = computed(() => agents.value.find(agent => agent.slug === options.conversationSlug.value) ?? null)
  /** Enabled agents other than the conversation agent; the ones the selected mode uses first. */
  const otherAgents = computed(() => agents.value
    .filter(agent => agent.enabled && agent.id !== conversationAgent.value?.id)
    .sort((a, b) => Number(Boolean(usageInMode(b))) - Number(Boolean(usageInMode(a)))))

  function usageInMode(agent: AgentDirectoryItemDto) {
    const version = options.modeVersionId.value
    return version ? agent.mode_usages?.find(usage => usage.mode_version_id === version) ?? null : null
  }

  /** What the agent was given here or in the agent center: a model, a route, or nothing. */
  function ownStrategy(agent: AgentDirectoryItemDto): NamedStrategy | null {
    const binding = agent.model_binding
    return binding ? named({ kind: binding.mode, provider_instance_id: binding.provider_instance_id, model: binding.model, route_purpose: binding.route_purpose }) : null
  }

  /** The model the agent was given here or in the agent center; null when it has none of its own. */
  function fixedChoice(agent: AgentDirectoryItemDto): ModelChoice | null {
    const own = ownStrategy(agent)
    return own?.kind === 'fixed' ? own.choice : null
  }

  /** What the agent runs on without a model of its own: its node in the selected mode, else its definition. */
  function configuredStrategy(agent: AgentDirectoryItemDto): NamedStrategy | null {
    return named(usageInMode(agent)?.model_strategy_override as StrategyLike) ?? named(agent.configured_strategy)
  }

  /**
   * The model the agent resolves to: its own, else the configured one. A route resolves through
   * Core's preview for the agent in the selected mode. Null when nothing names a model — the
   * default model at the root and the spawning agent's model below it; the directory preview,
   * computed without a parent, would say "the default" for both.
   */
  function effectiveChoice(agent: AgentDirectoryItemDto): ModelChoice | null {
    const strategy = ownStrategy(agent) ?? configuredStrategy(agent)
    if (!strategy) return null
    if (strategy.kind === 'fixed') return strategy.choice
    const usage = usageInMode(agent)
    const selection = usage ? agent.effective_previews?.[`${usage.mode_slug}:${usage.node_key}`]?.expected_selection : null
    return selection?.provider_instance_id && selection.available !== false
      ? { provider_instance_id: selection.provider_instance_id, model: selection.model ?? null }
      : null
  }

  async function load() {
    const sequence = ++read
    const scope = epoch
    try {
      const rows = await api.listAgents()
      // A newer read already landed, or the scope changed while this one was out.
      if (scope !== epoch || sequence < applied) return
      applied = sequence
      agents.value = Array.isArray(rows) ? rows : []
      loaded.value = true
      error.value = false
    } catch {
      // Keeps what was last read; only an unread scope reports the failure.
      if (scope === epoch && !loaded.value) error.value = true
    }
  }

  /**
   * Give an agent a model, or (null) take its own model away so it runs on what it is configured
   * with. The binding's tool-scope override is sent back as it was: the endpoint replaces the
   * whole binding, and leaving it out would clear a tool scope set elsewhere. True when saved.
   */
  async function setModel(agent: AgentDirectoryItemDto, choice: ModelChoice | null): Promise<boolean> {
    if (busy.value || !loaded.value) return false
    const toolScope = agent.model_binding?.tool_scope_override
    busy.value = true
    try {
      await api.putAgentRuntimeBinding(agent.id, {
        mode: choice ? 'fixed' : 'inherit',
        provider_instance_id: choice?.provider_instance_id ?? null,
        model: choice?.model ?? null,
        ...(Array.isArray(toolScope) ? { tool_scope: toolScope } : {}),
      })
      return true
    } catch (error) {
      notify.error(error, { title: t('composer.agentModels.failed', { agent: agent.display_name }) })
      return false
    } finally {
      await load()
      busy.value = false
    }
  }

  watch(selectedStorage, () => {
    epoch++
    loaded.value = false
    error.value = false
    void load()
  })
  return { agents, loaded, error, busy, load, conversationAgent, otherAgents, usageInMode, ownStrategy, fixedChoice, configuredStrategy, effectiveChoice, setModel }
}

export type AgentModels = ReturnType<typeof useAgentModels>
