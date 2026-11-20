<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { GitBranch, RefreshCw, ShieldCheck } from '@lucide/vue'
import { api as baseApi, type AgentDefinitionDto, type AgentViewDto, type PackI18n, type WorkspaceDefaultsDto } from '@/api'
import { scopedApi } from '@/lib/storageScope'
import { pickLocalized } from '@/lib/packI18n'
import { useHostAccess } from '@/lib/hostAccess'
const api = scopedApi(baseApi, () => 'user')
const { canAccessBackend } = useHostAccess()

/**
 * Governance roles panel (docs/app-core-ui.md §4.7).
 *
 * git_steward (operation, no tools by design) and worker.git (execution,
 * Git tool scope) are distinct roles and are always shown separately. The
 * effective tool intersection shown here is agent.allowed_tools ∩ the
 * manifest-provided catalog; tools missing from the live provider render as
 * unavailable in the manifest instead of being silently dropped.
 */
const { t, te, locale } = useI18n()
const layerLabel = (layer: string) => (te(`orchestrationPanel.layer.${layer}`) ? t(`orchestrationPanel.layer.${layer}`) : layer)
const statusLabel = (status: string) => (te(`agentCenter.filter.${status}`) ? t(`agentCenter.filter.${status}`) : status)

type RoleAgentLike = AgentDefinitionDto | (AgentViewDto & { i18n?: PackI18n | null })

const props = defineProps<{
  /** Live tool ids from Core/Tool Provider manifest. */
  manifestToolIds: string[]
  /**
   * Preloaded agents from parent to avoid duplicate 1+N definition fetches. Presence of the
   * prop (even as an empty array) is the signal — an empty list means "the parent already
   * asked and got nothing", which must not trigger a second identical request.
   */
  agents?: RoleAgentLike[]
}>()

const fetchedAgents = ref<RoleAgentLike[]>([])
const defaults = ref<WorkspaceDefaultsDto | null>(null)
const loading = ref(false)
const loadError = ref<string | null>(null)

/**
 * The governance roles this surface is responsible for. `operation` is the layer Core writes
 * today; `planning` is the legacy spelling some packs still carry. Both name the same seat,
 * so both are listed and matched — a legacy proposal must not silently disappear.
 */
const GOVERNANCE_ROLES = ['git_steward', 'worker.git']

const LAYER_ALIASES: Record<string, string> = { operation: 'operation', planning: 'operation' }

const sourceAgents = computed<RoleAgentLike[]>(() => props.agents ?? fetchedAgents.value)

function agentsProvided(): boolean {
  return props.agents !== undefined
}

function normalizeLayer(layer?: string | null): string {
  const key = String(layer ?? '').trim().toLowerCase()
  return LAYER_ALIASES[key] ?? key
}

function matchesRole(agent: RoleAgentLike, role: string): boolean {
  const wanted = role.toLowerCase()
  return [agent.slug, agent.name, agent.role, agent.agent_type]
    .some((value) => String(value ?? '').trim().toLowerCase() === wanted)
}

const roleAgents = computed(() =>
  GOVERNANCE_ROLES.map((role) => sourceAgents.value.find((a) => matchesRole(a, role))).filter(
    (a): a is RoleAgentLike => Boolean(a),
  ),
)

/** Every governance-layer agent, so the empty state can name what Core actually has. */
const governanceLayerAgents = computed(() =>
  sourceAgents.value.filter((a) => normalizeLayer(a.layer) === 'operation'),
)

const missingRoles = computed(() =>
  GOVERNANCE_ROLES.filter((role) => !roleAgents.value.some((agent) => matchesRole(agent, role))),
)

function displayName(agent: RoleAgentLike): string {
  const baseline = agent.display_name ?? agent.slug ?? agent.name ?? agent.id
  return pickLocalized(agent.i18n, locale.value, 'display_name', baseline) ?? baseline
}

/** Core stores model_strategy as JSON ({ kind: ... }) or a bare string. */
function modelStrategyLabel(agent: RoleAgentLike): string {
  const ms = agent.model_strategy
  if (!ms) return 'inherit'
  if (typeof ms === 'string') return ms
  if (typeof ms === 'object' && typeof (ms as Record<string, unknown>).kind === 'string') {
    return String((ms as Record<string, unknown>).kind)
  }
  return 'inherit'
}

function declaredTools(agent: RoleAgentLike): string[] {
  if (Array.isArray(agent.tool_scope)) return agent.tool_scope
  if (typeof agent.tool_scope === 'string') return [agent.tool_scope]
  return agent.allowed_tools ?? []
}

interface ToolRow {
  tool: string
  available: boolean
}

function effectiveTools(agent: RoleAgentLike): ToolRow[] {
  const declared = declaredTools(agent)
  if (declared.includes('*')) {
    // Wildcard passes through to whatever the manifest currently serves.
    return props.manifestToolIds.map((id) => ({ tool: id, available: true }))
  }
  return declared.map((tool) => ({ tool, available: props.manifestToolIds.includes(tool) || tool === '*' }))
}

const roleEntries = computed(() =>
  roleAgents.value.map((agent) => {
    const tools = effectiveTools(agent)
    return {
      agent,
      declaredEmpty: declaredTools(agent).length === 0,
      tools,
      availableCount: tools.filter((row) => row.available).length,
    }
  }),
)

async function load(): Promise<void> {
  if (!canAccessBackend.value) return
  loading.value = true
  loadError.value = null
  try {
    // Only fetch the definitions when the parent did not hand them over. `undefined` means
    // "nobody loaded them"; `[]` means "loaded, and there are none" — refetching on `[]` is
    // exactly the duplicate 1+N the prop exists to prevent.
    const needsAgents = !agentsProvided()
    const [all, ws] = await Promise.all([
      needsAgents ? api.listAgentDefinitions() : Promise.resolve(null),
      api.getWorkspaceDefaults().catch(() => null),
    ])
    if (needsAgents) {
      fetchedAgents.value = Array.isArray(all) ? all : []
    }
    defaults.value = ws
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch(canAccessBackend, (ready) => {
  if (ready) void load()
})

/**
 * A later parent load fills the prop after this panel has already mounted; adopting it
 * (rather than refetching) is what keeps the panel in step with the Agent Center directory.
 */
watch(
  () => props.agents,
  () => {
    if (agentsProvided()) loadError.value = null
  },
)
</script>

<template>
  <div class="gov-roles" data-testid="gov-roles">
    <header class="gov-roles__head">
      <h2><ShieldCheck class="size-4" /> {{ t('agentCenter.governanceRoles') }}</h2>
      <span v-if="defaults?.status" class="gov-roles__defaults" data-testid="workspace-defaults-status">
        {{ t('agentCenter.defaultsActive') }}: {{ statusLabel(defaults.status) }}
        <span v-if="defaults.revision != null">· {{ t('agentCenter.revisionShort', { revision: defaults.revision }) }}</span>
      </span>
    </header>

    <div v-if="loadError" class="gov-roles__error" role="alert">
      <span>{{ loadError }}</span>
      <button type="button" class="gov-roles__retry" :disabled="loading || !canAccessBackend" @click="void load()">
        <RefreshCw :size="12" />
        <span>{{ t('settings.retry') }}</span>
      </button>
    </div>
    <!--
      The empty state names both what is missing AND what Core actually has on the
      governance layer, so "no roles" is never a dead end: an operator can see the seats
      that exist and the two that were expected.
    -->
    <div v-else-if="!loading && !roleEntries.length" class="gov-roles__empty" data-testid="gov-roles-empty">
      <p class="gov-roles__empty-lead">
        {{ t('agentCenter.governanceRolesMissing') }}
        <code v-for="role in missingRoles" :key="role">{{ role }}</code>
      </p>
      <div v-if="governanceLayerAgents.length" class="gov-roles__present" data-testid="gov-roles-present">
        <span class="gov-roles__present-label">{{ t('settings.agentLayerPlanning') }}</span>
        <ul>
          <li v-for="agent in governanceLayerAgents" :key="agent.id">
            <strong>{{ displayName(agent) }}</strong>
            <code>{{ agent.slug ?? agent.name ?? agent.id }}</code>
          </li>
        </ul>
      </div>
      <p v-else class="quiet gov-roles__none">{{ t('agentCenter.empty.noAgents') }}</p>
    </div>

    <div class="gov-roles__grid">
      <!-- git_steward: operation layer, deliberately tool-less -->
      <section v-for="entry in roleEntries" :key="entry.agent.id" class="gov-role-card" data-testid="gov-role-card">
        <div class="gov-role-card__head">
          <component :is="(entry.agent.layer === 'operation' || entry.agent.layer === 'planning') ? ShieldCheck : GitBranch" class="size-4" />
          <strong>{{ displayName(entry.agent) }}</strong>
          <span class="gov-role-card__layer">{{ layerLabel(entry.agent.layer) }}</span>
          <span v-if="entry.agent.status" class="gov-role-card__status">{{ statusLabel(entry.agent.status) }}</span>
        </div>

        <p v-if="(entry.agent.layer === 'operation' || entry.agent.layer === 'planning') && entry.declaredEmpty" class="gov-role-card__note" data-testid="steward-no-tools">
          {{ t('agentCenter.stewardNoTools') }}
        </p>

        <dl class="gov-role-card__facts">
          <div><dt>{{ t('agentCenter.facts.modelStrategy') }}</dt><dd data-testid="model-strategy">{{ modelStrategyLabel(entry.agent) }}</dd></div>
          <div v-if="entry.agent.model_route_purpose"><dt>{{ t('agentCenter.facts.route') }}</dt><dd>{{ entry.agent.model_route_purpose }}</dd></div>
        </dl>

        <div class="gov-role-card__tools">
          <h4>{{ t('agentCenter.effectiveTools', { count: entry.availableCount }) }}</h4>
          <ul data-testid="effective-tools">
            <li v-for="row in entry.tools" :key="row.tool" :class="{ 'tool-unavailable': !row.available }">
              <code>{{ row.tool }}</code>
              <span v-if="!row.available" class="tool-unavailable-label">{{ t('agentCenter.toolUnavailableInManifest') }}</span>
            </li>
            <li v-if="!entry.tools.length" class="tool-none">{{ t('common.none') }}</li>
          </ul>
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.gov-roles {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px 16px;
  border: 1px solid var(--border-muted);
  border-radius: 10px;
  background: var(--surface-section);
}
.gov-roles__head { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px; }
.gov-roles__head h2 { display: flex; align-items: center; gap: 6px; font-size: 14px; font-weight: 700; margin: 0; }
.gov-roles__defaults { font-size: 11px; color: var(--text-secondary); }
.gov-roles__error {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 10px 12px;
  border-radius: 8px;
  background: var(--bg-status-danger);
  color: var(--accent-danger);
  font-size: 13px;
}
.gov-roles__retry {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 4px 10px;
  border-radius: 6px;
  border: 1px solid color-mix(in srgb, var(--accent-danger) 35%, transparent);
  background: var(--surface-raised);
  color: var(--text-primary);
  font-size: 11px;
  cursor: pointer;
}
.gov-roles__empty {
  margin: 0;
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  padding: 8px 12px;
  border-radius: 8px;
  border: 1px dashed var(--border-muted);
  background: var(--surface-raised);
  font-size: 12px;
  color: var(--text-secondary);
}
.gov-roles__empty code {
  padding: 2px 8px;
  border-radius: 5px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-primary);
  font-family: var(--font-mono);
  font-size: 11px;
}
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
