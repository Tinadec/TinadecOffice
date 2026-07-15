<script setup lang="ts">
import { ArrowDown, Cpu, GitBranch, Workflow } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { AgentCandidateDto, AgentProfileDto, AgentRuntimeBindingDto, ModelProviderInstanceDto, ModelRouteDto } from '../api'

const props = defineProps<{
  agents: readonly AgentProfileDto[]
  candidates: readonly AgentCandidateDto[]
  providers: readonly ModelProviderInstanceDto[]
  routes: readonly ModelRouteDto[]
  runtimeBindings: Readonly<Record<string, AgentRuntimeBindingDto>>
  selectedAgentId: string
  agentLabels: Readonly<Record<string, string>>
  candidateLabels: Readonly<Record<string, string>>
}>()

const emit = defineEmits<{
  'select-agent': [id: string]
  'configure-agent': [id: string]
}>()

const { t } = useI18n()
const planningAgents = computed(() => props.agents.filter((agent) => agent.layer === 'planning'))
const executionAgents = computed(() => props.agents.filter((agent) => agent.layer === 'execution'))

function runtimeParts(agent: AgentProfileDto) {
  const binding = props.runtimeBindings[agent.id]
  if (binding) {
    if (binding.runtime_kind === 'unresolved') {
      return { provider: '', model: t('settings.runtimeUnresolved') }
    }
    return {
      provider: binding.provider_display_name ?? binding.runtime_id ?? binding.route_purpose,
      model: binding.model_id ?? binding.runtime_kind.toUpperCase()
    }
  }

  const route = props.routes.find((item) => item.purpose === agent.model_route_purpose)
  const provider = props.providers.find((item) => item.id === route?.provider_instance_id)
  return {
    provider: provider?.display_name ?? '',
    model: route?.model ?? provider?.model ?? agent.model_route_purpose
  }
}
</script>

<template>
  <div class="agent-topology-container" :aria-label="t('settings.agentTopology')">
    <section class="agent-topology-layer planning">
      <header>
        <span><Workflow :size="15" />{{ t('settings.planningLayer') }}</span>
        <strong>{{ planningAgents.length }}</strong>
      </header>
      <div class="agent-topology-grid">
        <button
          v-for="agent in planningAgents"
          :key="agent.id"
          type="button"
          class="agent-topology-node"
          :class="{ selected: selectedAgentId === agent.id, disabled: !agent.enabled }"
          :title="t('settings.clickToConfig')"
          @click="emit('select-agent', agent.id)"
          @dblclick="emit('configure-agent', agent.id)"
        >
          <span class="agent-topology-node-icon"><Workflow :size="15" /></span>
          <span class="agent-topology-node-copy">
            <strong>{{ agentLabels[agent.id] ?? agent.name }}</strong>
            <span>{{ runtimeParts(agent).provider || t('settings.runtimeUnresolved') }}</span>
            <small>{{ runtimeParts(agent).model }}</small>
          </span>
        </button>
      </div>
    </section>

    <div class="agent-topology-flow" aria-hidden="true">
      <ArrowDown :size="16" />
      <span>{{ t('settings.dispatchFlow') }}</span>
    </div>

    <section class="agent-topology-layer execution">
      <header>
        <span><Cpu :size="15" />{{ t('settings.executionLayer') }}</span>
        <strong>{{ executionAgents.length }}</strong>
      </header>
      <div class="agent-topology-grid">
        <button
          v-for="agent in executionAgents"
          :key="agent.id"
          type="button"
          class="agent-topology-node"
          :class="{ selected: selectedAgentId === agent.id, disabled: !agent.enabled }"
          :title="t('settings.clickToConfig')"
          @click="emit('select-agent', agent.id)"
          @dblclick="emit('configure-agent', agent.id)"
        >
          <span class="agent-topology-node-icon"><Cpu :size="15" /></span>
          <span class="agent-topology-node-copy">
            <strong>{{ agentLabels[agent.id] ?? agent.name }}</strong>
            <span>{{ runtimeParts(agent).provider || t('settings.runtimeUnresolved') }}</span>
            <small>{{ runtimeParts(agent).model }}</small>
          </span>
        </button>
      </div>
    </section>

    <section v-if="candidates.length > 0" class="agent-topology-layer candidates">
      <header>
        <span><GitBranch :size="15" />{{ t('settings.evolutionCandidates') }}</span>
        <strong>{{ candidates.length }}</strong>
      </header>
      <div class="agent-topology-grid">
        <article v-for="candidate in candidates" :key="candidate.id" class="agent-topology-node candidate">
          <span class="agent-topology-node-icon"><GitBranch :size="15" /></span>
          <span class="agent-topology-node-copy">
            <strong>{{ candidateLabels[candidate.id] ?? candidate.name }}</strong>
            <span>{{ candidate.layer === 'execution' ? t('settings.executionLayer') : t('settings.planningLayer') }}</span>
            <small>{{ t('settings.candidateProposed') }}</small>
          </span>
        </article>
      </div>
    </section>
  </div>
</template>
