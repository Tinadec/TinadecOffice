<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { GitBranch, ShieldCheck } from '@lucide/vue'
import { api, type AgentDefinitionDto, type WorkspaceDefaultsDto } from '@/api'

/**
 * Governance roles panel (docs/app-core-ui.md §4.7).
 *
 * git_steward (operation, no tools by design) and worker.git (execution,
 * Git tool scope) are distinct roles and are always shown separately. The
 * effective tool intersection shown here is agent.allowed_tools ∩ the
 * manifest-provided catalog; tools missing from the live provider render as
 * "provider unavailable" instead of being silently dropped.
 */
const { t } = useI18n()

const props = defineProps<{
  /** Live tool ids from Core/Tool Provider manifest. */
  manifestToolIds: string[]
}>()

const agents = ref<AgentDefinitionDto[]>([])
const defaults = ref<WorkspaceDefaultsDto | null>(null)
const loading = ref(false)
const loadError = ref<string | null>(null)

const GOVERNANCE_ROLES = ['git_steward', 'worker.git']

const roleAgents = computed(() =>
  GOVERNANCE_ROLES.map((role) =>
    agents.value.find((a) => a.slug === role || a.name === role || a.role === role || a.agent_type === role),
  ).filter((a): a is AgentDefinitionDto => Boolean(a)),
)

function displayName(agent: AgentDefinitionDto): string {
  return agent.display_name ?? agent.slug ?? agent.name ?? agent.id
}

/** Core stores model_strategy as JSON ({ kind: ... }) or a bare string. */
function modelStrategyLabel(agent: AgentDefinitionDto): string {
  const ms = agent.model_strategy
  if (!ms) return 'inherit'
  if (typeof ms === 'string') return ms
  if (typeof ms === 'object' && typeof (ms as Record<string, unknown>).kind === 'string') {
    return String((ms as Record<string, unknown>).kind)
  }
  return 'inherit'
}

function declaredTools(agent: AgentDefinitionDto): string[] {
  if (Array.isArray(agent.tool_scope)) return agent.tool_scope
  if (typeof agent.tool_scope === 'string') return [agent.tool_scope]
  return agent.allowed_tools ?? []
}

interface ToolRow {
  tool: string
  available: boolean
}

function effectiveTools(agent: AgentDefinitionDto): ToolRow[] {
  const declared = declaredTools(agent)
  if (declared.includes('*')) {
    // Wildcard passes through to whatever the manifest currently serves.
    return props.manifestToolIds.map((id) => ({ tool: id, available: true }))
  }
  return declared.map((tool) => ({ tool, available: props.manifestToolIds.includes(tool) || tool === '*' }))
}

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    const [all, ws] = await Promise.all([
      api.listAgentDefinitions().catch(() => [] as AgentDefinitionDto[]),
      api.getWorkspaceDefaults().catch(() => null),
    ])
    agents.value = all
    defaults.value = ws
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

onMounted(load)
</script>

<template>
  <div class="gov-roles" data-testid="gov-roles">
    <header class="gov-roles__head">
      <h2><ShieldCheck class="size-4" /> {{ t('agentCenter.governanceRoles', 'Git governance roles') }}</h2>
      <span v-if="defaults?.status" class="gov-roles__defaults" data-testid="workspace-defaults-status">
        {{ t('agentCenter.defaultsActive', 'defaults') }}: {{ defaults.status }}
        <span v-if="defaults.revision != null">· rev {{ defaults.revision }}</span>
      </span>
    </header>

    <div v-if="loadError" class="gov-roles__error">{{ loadError }}</div>
    <p v-else-if="!loading && !roleAgents.length" class="gov-roles__empty" data-testid="gov-roles-empty">
      {{ t('agentCenter.governanceRolesMissing', 'Baseline governance roles are not seeded yet.') }}
    </p>

    <div class="gov-roles__grid">
      <!-- git_steward: operation layer, deliberately tool-less -->
      <section v-for="agent in roleAgents" :key="agent.id" class="gov-role-card" data-testid="gov-role-card">
        <div class="gov-role-card__head">
          <component :is="agent.layer === 'operation' ? ShieldCheck : GitBranch" class="size-4" />
          <strong>{{ displayName(agent) }}</strong>
          <span class="gov-role-card__layer">{{ agent.layer }}</span>
          <span v-if="agent.status" class="gov-role-card__status">{{ agent.status }}</span>
        </div>

        <p v-if="agent.layer === 'operation' && !(agent.allowed_tools ?? []).length" class="gov-role-card__note" data-testid="steward-no-tools">
          {{ t('agentCenter.stewardNoTools', 'No direct tools — this is a design decision, not a configuration error. It reviews diffs and proposes commit plans; execution happens through approved user/worker actions.') }}
        </p>

        <dl class="gov-role-card__facts">
          <div><dt>model_strategy</dt><dd data-testid="model-strategy">{{ modelStrategyLabel(agent) }}</dd></div>
          <div v-if="agent.model_route_purpose"><dt>route</dt><dd>{{ agent.model_route_purpose }}</dd></div>
        </dl>

        <div class="gov-role-card__tools">
          <h4>{{ t('agentCenter.effectiveTools', 'Effective tools (declared ∩ manifest)') }}</h4>
          <ul data-testid="effective-tools">
            <li v-for="row in effectiveTools(agent)" :key="row.tool" :class="{ 'tool-unavailable': !row.available }">
              <code>{{ row.tool }}</code>
              <span v-if="!row.available" class="tool-unavailable-label">{{ t('agentCenter.providerUnavailable', 'provider unavailable') }}</span>
            </li>
            <li v-if="!effectiveTools(agent).length" class="tool-none">{{ t('common.none', 'None') }}</li>
          </ul>
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.gov-roles { display: flex; flex-direction: column; gap: 12px; }
.gov-roles__head { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px; }
.gov-roles__head h2 { display: flex; align-items: center; gap: 6px; font-size: 14px; font-weight: 700; margin: 0; }
.gov-roles__defaults { font-size: 11px; color: var(--text-secondary); }
.gov-roles__error { padding: 10px 12px; border-radius: 8px; background: var(--bg-status-danger); color: var(--accent-danger); font-size: 13px; }
.gov-roles__empty { font-size: 13px; color: var(--text-secondary); }
.gov-roles__grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 12px; }

.gov-role-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px 14px;
  border-radius: 10px;
  background: var(--surface-section);
}

.gov-role-card__head { display: flex; align-items: center; gap: 8px; }
.gov-role-card__layer,
.gov-role-card__status {
  font-size: 10px;
  font-weight: 600;
  padding: 1px 6px;
  border-radius: 3px;
  background: var(--bg-status-neutral);
  color: var(--text-secondary);
}

.gov-role-card__note {
  margin: 0;
  padding: 8px 10px;
  border-radius: 6px;
  background: var(--bg-status-info-soft, var(--surface-raised));
  font-size: 12px;
  color: var(--text-secondary);
}

.gov-role-card__facts { display: flex; gap: 16px; margin: 0; }
.gov-role-card__facts div { display: flex; gap: 4px; }
.gov-role-card__facts dt { color: var(--text-secondary); font-size: 11px; }
.gov-role-card__facts dd { margin: 0; font-size: 12px; }

.gov-role-card__tools h4 { font-size: 11px; font-weight: 600; color: var(--text-secondary); margin: 0 0 4px; }
.gov-role-card__tools ul { list-style: none; margin: 0; padding: 0; display: flex; flex-wrap: wrap; gap: 6px; }
.gov-role-card__tools li { display: inline-flex; align-items: center; gap: 5px; font-size: 11px; }
.gov-role-card__tools code { padding: 1px 5px; border-radius: 3px; background: var(--surface-raised); }
.tool-unavailable code { opacity: 0.55; text-decoration: line-through; }
.tool-unavailable-label { color: var(--accent-warning); }
.tool-none { color: var(--text-secondary); }
</style>
