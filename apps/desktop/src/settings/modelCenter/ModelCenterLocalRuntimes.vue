<script setup lang="ts">
import { computed, nextTick } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertTriangle, Plus, RefreshCw, Settings2, Terminal, Trash2, Workflow } from '@lucide/vue'
import { UiBadge, UiButton } from '@/components/ui'
import type {
  CliDiscoveryCandidateDto,
  HarnessChannelDto,
  ModelCenterOverviewDto,
  ModelProviderInstanceDto,
} from '@/api'
import { discoveryAffordanceLabelKey } from '@/runtimeCenterView'
import { findTemplate } from '@/providerTemplates'

export type LocalRuntimeChannelKey = 'cli' | 'tui' | 'acp'

const props = defineProps<{
  activeChannel: LocalRuntimeChannelKey
  overview: ModelCenterOverviewDto | null
  providers: ModelProviderInstanceDto[]
  busy: boolean
  cliDiscoveryCandidates: CliDiscoveryCandidateDto[]
  cliDiscoveryLoading: boolean
  cliDiscoveryLoaded: boolean
  statusLabel: (status: string) => string
  statusVariant: (status: string) => 'default' | 'secondary' | 'destructive' | 'outline'
}>()

const emit = defineEmits<{
  'update:activeChannel': [channel: LocalRuntimeChannelKey]
  discover: []
  'connect-cli': [candidate: CliDiscoveryCandidateDto, channel?: HarnessChannelDto]
  'configure-cli': [candidate: CliDiscoveryCandidateDto, channel?: HarnessChannelDto]
  'add-custom-acp': []
  'edit-provider': [provider: ModelProviderInstanceDto]
  'delete-provider': [providerId: string]
}>()

const { t, te } = useI18n()

function safeLabel(key: string, fallback: string, params?: Record<string, unknown>): string {
  return te(key) ? String(t(key, params ?? {})) : fallback
}

function templateForDriver(driver?: string | null) {
  return driver ? findTemplate(driver) : undefined
}

function acpCapabilityLabel(cap: string): string {
  const normalized = cap.toLowerCase().replace(/[\s_-]+/g, '')
  if (normalized === 'loadsession') return safeLabel('settings.acpCapLoadSession', 'Load Session')
  if (normalized === 'imageinput' || normalized === 'image') return safeLabel('settings.acpCapImageInput', 'Image Input')
  if (normalized === 'sessionfork' || normalized === 'forksession') return safeLabel('settings.acpCapSessionFork', 'Session Fork')
  return cap
}

function runtimeDiagnosticMessage(providerId?: string | null, fallbackMessage?: string | null): string | null {
  const provider = providerById(providerId)
  const msg = provider?.status_message || fallbackMessage || ''
  if (!msg || ['configured', 'disabled'].includes(provider?.status ?? '')) return null
  return msg
}

function candidateProbeHint(candidate: CliDiscoveryCandidateDto): string | null {
  const ext = candidate as CliDiscoveryCandidateDto & { probe_hint?: string | null }
  return ext.probe_hint || probeNote(candidate)
}

interface AcpCapsShape {
  load_session?: boolean
  image_input?: boolean
  session_fork?: boolean
}

function acpCapabilityTags(
  rawCapabilities: string[] | undefined,
  providerId?: string | null,
  explicitCaps?: AcpCapsShape | null,
): string[] {
  const tags = new Set<string>()
  const provider = providerById(providerId) as (ModelProviderInstanceDto & { acp_capabilities?: AcpCapsShape | null }) | undefined
  const caps = explicitCaps ?? provider?.acp_capabilities ?? null
  if (caps?.load_session) tags.add('load_session')
  if (caps?.image_input) tags.add('image_input')
  if (caps?.session_fork) tags.add('session_fork')
  for (const cap of rawCapabilities ?? []) {
    if (cap) tags.add(cap)
  }
  return Array.from(tags).slice(0, 6)
}

const runtimeChannelKeys: readonly LocalRuntimeChannelKey[] = ['cli', 'tui', 'acp']

const runtimeChannels = computed(() => [
  { key: 'cli' as const, label: t('settings.centerCli'), count: props.overview?.cli_runtimes.length ?? 0 },
  { key: 'tui' as const, label: t('settings.centerTui'), count: props.overview?.tui_runtimes.length ?? 0 },
  { key: 'acp' as const, label: t('settings.centerAcp'), count: props.overview?.acp_runtimes.length ?? 0 },
])

const totalLocalCount = computed(() =>
  runtimeChannels.value.reduce((sum, item) => sum + item.count, 0),
)

function acpRuntimeSourceLabel(source: string) {
  if (source === 'provider_instance') return t('settings.harnessProviderInstance')
  if (source === 'legacy_provider') return t('settings.legacyProvider')
  return t('settings.acpAdapter')
}

function drivableChannels(candidate: CliDiscoveryCandidateDto): HarnessChannelDto[] {
  return (candidate.channels ?? []).filter((channel) => channel.drivable)
}

function blockedChannels(candidate: CliDiscoveryCandidateDto): HarnessChannelDto[] {
  return (candidate.channels ?? []).filter((channel) => !channel.drivable)
}

function channelLabel(fact: HarnessChannelDto): string {
  return t(discoveryAffordanceLabelKey(fact))
}

function channelReason(channel: HarnessChannelDto, candidate: CliDiscoveryCandidateDto): string {
  const key = `settings.channelReason.${channel.reason_code}`
  return channel.reason_code && te(key)
    ? t(key, { harness: candidate.display_name })
    : channel.reason ?? t('settings.channelNotDrivableDefault')
}

function probeNote(candidate: CliDiscoveryCandidateDto): string | null {
  const key = `settings.probeNote.${candidate.probe_note_code}`
  return candidate.probe_note_code && te(key)
    ? t(key, candidate.probe_note_data ?? {})
    : candidate.probe_note ?? null
}

function providerById(id?: string | null): ModelProviderInstanceDto | undefined {
  if (!id) return undefined
  return props.providers.find((provider) => provider.id === id)
}

async function onChannelKeydown(event: KeyboardEvent) {
  const index = runtimeChannelKeys.indexOf(props.activeChannel)
  if (index < 0) return
  let target = -1
  if (event.key === 'ArrowRight') target = (index + 1) % runtimeChannelKeys.length
  else if (event.key === 'ArrowLeft') target = (index - 1 + runtimeChannelKeys.length) % runtimeChannelKeys.length
  else if (event.key === 'Home') target = 0
  else if (event.key === 'End') target = runtimeChannelKeys.length - 1
  if (target < 0) return
  event.preventDefault()
  const list = (event.currentTarget as HTMLElement | null)?.closest<HTMLElement>('[role="tablist"]')
  emit('update:activeChannel', runtimeChannelKeys[target])
  await nextTick()
  list?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]')?.focus()
}
</script>

<template>
  <section class="local-runtimes-block" data-testid="model-center-local-runtimes">
    <div class="local-runtimes-head">
      <div>
        <h3>{{ t('settings.localRuntimesTitle') }}</h3>
        <p>{{ t('settings.localRuntimesHint') }}</p>
      </div>
      <div class="local-runtimes-head-actions">
        <UiBadge variant="outline">{{ totalLocalCount }}</UiBadge>
        <UiButton
          variant="outline"
          size="sm"
          :disabled="cliDiscoveryLoading"
          @click="emit('discover')"
        >
          <RefreshCw :size="14" :class="{ 'animate-spin': cliDiscoveryLoading }" />
          <span>{{ cliDiscoveryLoading ? t('settings.discoveringCli') : t('settings.discoverCli') }}</span>
        </UiButton>
      </div>
    </div>

    <div class="model-center-subtabs" role="tablist" :aria-label="t('settings.centerRuntime')">
      <button
        v-for="section in runtimeChannels"
        :key="section.key"
        type="button"
        role="tab"
        :data-runtime-channel="section.key"
        :aria-selected="activeChannel === section.key"
        :tabindex="activeChannel === section.key ? 0 : -1"
        :class="{ active: activeChannel === section.key }"
        @click="emit('update:activeChannel', section.key)"
        @keydown="onChannelKeydown"
      >
        <span>{{ section.label }}</span>
        <UiBadge variant="secondary">{{ section.count }}</UiBadge>
      </button>
    </div>

    <!-- Discovery Candidates Panel (shown when discovery ran, across channels) -->
    <div v-if="cliDiscoveryLoaded" class="cli-discovery-panel">
      <div class="cli-discovery-header">
        <strong>{{ t('settings.cliDiscoveryTitle') }}</strong>
        <p>{{ t('settings.cliDiscoveryHint') }}</p>
      </div>
      <div v-if="cliDiscoveryCandidates.length === 0" class="cli-discovery-empty">
        {{ t('settings.cliDiscoveryEmpty') }}
      </div>
      <div v-else class="cli-discovery-grid">
        <div
          v-for="candidate in cliDiscoveryCandidates"
          :key="candidate.driver"
          class="cli-discovery-card"
          :class="{
            'is-found': candidate.status === 'found',
            'is-configured': candidate.status === 'configured',
            'is-missing': candidate.status === 'missing',
          }"
        >
          <div class="cli-discovery-info">
            <div class="cli-discovery-name-row">
              <span
                v-if="templateForDriver(candidate.driver)?.icon"
                class="local-runtime-brand-icon"
                :style="{ color: templateForDriver(candidate.driver)?.brand_color, backgroundColor: templateForDriver(candidate.driver)?.brand_bg }"
                v-html="templateForDriver(candidate.driver)?.icon"
              />
              <Terminal v-else :size="15" />
              <strong>{{ candidate.display_name }}</strong>
              <span class="cli-discovery-driver">{{ candidate.driver }}</span>
            </div>
            <code v-if="candidate.binary_path" class="cli-discovery-path" :title="candidate.binary_path">
              {{ candidate.binary_path }}
            </code>
            <span
              v-if="candidateProbeHint(candidate)"
              class="cli-discovery-status-text muted"
              :title="probeNote(candidate) === candidate.probe_note ? undefined : candidate.probe_note ?? undefined"
            >
              {{ candidateProbeHint(candidate) }}
            </span>
            <span v-else-if="candidate.status === 'missing'" class="cli-discovery-status-text muted">
              {{ t('settings.notDetected') }}
            </span>
            <div
              v-if="acpCapabilityTags(undefined, undefined, (candidate as unknown as { acp_capabilities?: AcpCapsShape | null }).acp_capabilities).length"
              class="local-runtime-caps"
            >
              <UiBadge
                v-for="cap in acpCapabilityTags(undefined, undefined, (candidate as unknown as { acp_capabilities?: AcpCapsShape | null }).acp_capabilities)"
                :key="cap"
                variant="secondary"
              >
                {{ acpCapabilityLabel(cap) }}
              </UiBadge>
            </div>
          </div>
          <div class="cli-discovery-action">
            <UiBadge v-if="candidate.status === 'configured'" variant="secondary">
              {{ t('settings.alreadyConnected') }}
            </UiBadge>
            <template v-if="candidate.status === 'found' || candidate.status === 'configured'">
              <UiButton
                v-for="channel in drivableChannels(candidate)"
                :key="channel.channel ?? channel.protocol"
                variant="default"
                size="sm"
                :disabled="cliDiscoveryLoading"
                @click="emit('connect-cli', candidate, channel)"
              >
                <Plus :size="13" />
                {{ t('settings.quickConnectChannel', { channel: channelLabel(channel) }) }}
              </UiButton>
              <UiButton
                variant="outline"
                size="sm"
                :disabled="cliDiscoveryLoading"
                @click="emit('configure-cli', candidate, drivableChannels(candidate)[0] ?? candidate.channels?.[0])"
              >
                <Settings2 :size="13" />
                {{ safeLabel('settings.configureHarness', 'Configure') }}
              </UiButton>
              <UiBadge
                v-for="channel in blockedChannels(candidate)"
                :key="`blocked-${channel.channel ?? channel.protocol}`"
                variant="outline"
                class="muted-badge"
                :title="channelReason(channel, candidate)"
              >
                {{ t('settings.channelNotDrivable', { channel: channelLabel(channel) }) }}
              </UiBadge>
              <UiButton
                v-if="drivableChannels(candidate).length === 0 && blockedChannels(candidate).length === 0"
                variant="default"
                size="sm"
                :disabled="cliDiscoveryLoading"
                @click="emit('connect-cli', candidate)"
              >
                <Plus :size="13" />
                {{ t('settings.quickConnect') }}
              </UiButton>
            </template>
            <UiBadge v-else variant="outline" class="muted-badge">
              {{ t('settings.notDetected') }}
            </UiBadge>
          </div>
        </div>
      </div>
    </div>

    <section v-if="activeChannel === 'cli'" class="center-resource-section local-channel-pane">
      <div class="center-resource-heading">
        <div>
          <h4>{{ t('settings.centerCli') }}</h4>
          <p>{{ t('settings.cliRuntimeHint') }}</p>
        </div>
      </div>
      <div class="center-resource-list">
        <article v-for="runtime in overview?.cli_runtimes ?? []" :key="runtime.runtime_id" class="center-resource-list-row">
          <div class="center-resource-primary">
            <Terminal :size="17" />
            <div>
              <strong>{{ runtime.display_name }}</strong>
              <span>{{ runtime.driver }} · {{ runtime.runtime_id }}</span>
            </div>
          </div>
          <div class="center-resource-paths">
            <code>{{ runtime.binary_path || t('settings.pathNotConfigured') }}</code>
            <code>{{ runtime.home_path || t('settings.workspaceNotConfigured') }}</code>
          </div>
          <UiBadge :variant="statusVariant(runtime.status)">{{ statusLabel(runtime.status) }}</UiBadge>
          <div class="center-resource-actions">
            <UiButton
              v-if="providerById(runtime.provider_instance_id)"
              variant="outline"
              size="sm"
              @click="emit('edit-provider', providerById(runtime.provider_instance_id)!)"
            >
              <Settings2 :size="14" />
              {{ t('settings.editConfig') }}
            </UiButton>
            <UiButton
              v-if="runtime.provider_instance_id"
              variant="ghost"
              size="icon"
              class="provider-delete-btn"
              :disabled="busy"
              :title="t('settings.delete')"
              @click="emit('delete-provider', runtime.provider_instance_id)"
            >
              <Trash2 :size="14" />
            </UiButton>
          </div>
        </article>
      </div>
      <div v-if="(overview?.cli_runtimes.length ?? 0) === 0" class="center-empty-state">
        <Terminal :size="20" />
        <span>{{ t('settings.noCliRuntimes') }}</span>
      </div>
    </section>

    <section v-if="activeChannel === 'tui'" class="center-resource-section local-channel-pane">
      <div class="center-resource-heading">
        <div>
          <h4>{{ t('settings.centerTui') }}</h4>
          <p>{{ t('settings.tuiRuntimeHint') }}</p>
        </div>
      </div>
      <p class="acp-permission-notice" role="note">
        {{ t('settings.tuiBackendUnavailable') }}
      </p>
      <div class="center-resource-list">
        <article v-for="runtime in overview?.tui_runtimes ?? []" :key="runtime.runtime_id" class="center-resource-list-row">
          <div class="center-resource-primary">
            <Terminal :size="17" />
            <div>
              <strong>{{ runtime.display_name }}</strong>
              <span>{{ runtime.driver }} · {{ runtime.protocol || t('settings.protocolUnknown') }}</span>
            </div>
          </div>
          <div class="center-resource-paths">
            <code>{{ runtime.binary_path || t('settings.pathNotConfigured') }}</code>
          </div>
          <UiBadge :variant="statusVariant(runtime.status)">{{ statusLabel(runtime.status) }}</UiBadge>
          <div class="center-resource-actions">
            <UiButton
              v-if="providerById(runtime.provider_instance_id)"
              variant="outline"
              size="sm"
              @click="emit('edit-provider', providerById(runtime.provider_instance_id)!)"
            >
              <Settings2 :size="14" />
              {{ t('settings.editConfig') }}
            </UiButton>
            <UiButton
              variant="ghost"
              size="icon"
              class="provider-delete-btn"
              :disabled="busy"
              :title="t('settings.delete')"
              @click="emit('delete-provider', runtime.provider_instance_id)"
            >
              <Trash2 :size="14" />
            </UiButton>
          </div>
        </article>
      </div>
      <div v-if="(overview?.tui_runtimes.length ?? 0) === 0" class="center-empty-state">
        <Terminal :size="20" />
        <span>{{ t('settings.noTuiRuntimes') }}</span>
      </div>
    </section>

    <section v-if="activeChannel === 'acp'" class="center-resource-section local-channel-pane">
      <div class="center-resource-heading">
        <div>
          <h4>{{ t('settings.centerAcp') }}</h4>
          <p>{{ t('settings.acpRuntimeHint') }}</p>
        </div>
        <UiButton variant="outline" size="sm" data-testid="add-custom-acp-btn" @click="emit('add-custom-acp')">
          <Plus :size="14" />
          {{ safeLabel('settings.addCustomAcp', 'Add Custom ACP') }}
        </UiButton>
      </div>
      <p class="acp-permission-notice" role="note">
        {{ t('settings.acpPermissionWarning') }}
      </p>
      <div class="center-resource-list">
        <article v-for="runtime in overview?.acp_runtimes ?? []" :key="runtime.runtime_id" class="center-resource-list-row">
          <div class="center-resource-primary">
            <span
              v-if="templateForDriver(providerById(runtime.provider_instance_id)?.driver)?.icon"
              class="local-runtime-brand-icon"
              :style="{
                color: templateForDriver(providerById(runtime.provider_instance_id)?.driver)?.brand_color,
                backgroundColor: templateForDriver(providerById(runtime.provider_instance_id)?.driver)?.brand_bg,
              }"
              v-html="templateForDriver(providerById(runtime.provider_instance_id)?.driver)?.icon"
            />
            <Workflow v-else :size="17" />
            <div>
              <strong>{{ runtime.display_name }}</strong>
              <span>{{ acpRuntimeSourceLabel(runtime.source) }} · {{ runtime.runtime_id }}</span>
              <span
                v-if="runtimeDiagnosticMessage(runtime.provider_instance_id)"
                class="local-runtime-diag"
              >
                <AlertTriangle :size="12" aria-hidden="true" />
                {{ runtimeDiagnosticMessage(runtime.provider_instance_id) }}
              </span>
            </div>
          </div>
          <div class="center-resource-paths">
            <code>{{ runtime.binary_path || t('settings.pathNotConfigured') }}</code>
          </div>
          <div class="center-resource-meta">
            <UiBadge
              v-for="capability in acpCapabilityTags(runtime.capabilities, runtime.provider_instance_id)"
              :key="capability"
              variant="secondary"
            >
              {{ acpCapabilityLabel(capability) }}
            </UiBadge>
          </div>
          <UiBadge :variant="statusVariant(runtime.status)">{{ statusLabel(runtime.status) }}</UiBadge>
          <div class="center-resource-actions">
            <UiButton
              v-if="providerById(runtime.provider_instance_id)"
              variant="outline"
              size="sm"
              @click="emit('edit-provider', providerById(runtime.provider_instance_id)!)"
            >
              <Settings2 :size="14" />
              {{ t('settings.editConfig') }}
            </UiButton>
            <UiButton
              v-if="runtime.provider_instance_id"
              variant="ghost"
              size="icon"
              class="provider-delete-btn"
              :disabled="busy"
              :title="t('settings.delete')"
              @click="emit('delete-provider', runtime.provider_instance_id)"
            >
              <Trash2 :size="14" />
            </UiButton>
          </div>
        </article>
      </div>
      <div v-if="(overview?.acp_runtimes.length ?? 0) === 0" class="center-empty-state">
        <Workflow :size="20" />
        <span>{{ t('settings.noAcpRuntimes') }}</span>
      </div>
    </section>
  </section>
</template>

<style scoped>
.local-runtimes-block {
  margin-top: 18px;
  padding-top: 16px;
  border-top: 1px solid var(--border-muted);
  display: grid;
  gap: 12px;
}
.local-runtimes-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}
.local-runtimes-head h3 {
  margin: 0 0 3px;
  font-size: 14px;
  font-weight: 600;
  color: var(--text-primary);
}
.local-runtimes-head p {
  margin: 0;
  font-size: 12px;
  color: var(--text-secondary);
}
.local-runtimes-head-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}
.local-channel-pane h4 {
  margin: 0 0 3px;
  font-size: 13px;
  font-weight: 600;
}
.cli-discovery-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 10px;
}
.cli-discovery-card {
  display: flex;
  flex-direction: column;
  align-items: stretch;
  justify-content: space-between;
  gap: 10px;
  min-width: 0;
  overflow: hidden;
}
.cli-discovery-info {
  min-width: 0;
  width: 100%;
}
.cli-discovery-name-row {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  flex-wrap: wrap;
}
.cli-discovery-path {
  display: block;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.cli-discovery-action {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  min-width: 0;
  width: 100%;
  flex-shrink: 1;
}
.cli-discovery-action :deep(button) {
  max-width: 100%;
  min-height: var(--control-height-sm, 30px);
  white-space: nowrap;
  flex-shrink: 0;
}
.cli-discovery-action :deep(svg) {
  flex-shrink: 0;
}
.cli-discovery-action :deep(.muted-badge) {
  max-width: 100%;
  white-space: normal;
  text-align: left;
}
.local-runtime-brand-icon {
  width: 22px;
  height: 22px;
  border-radius: 6px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
}
.local-runtime-brand-icon :deep(svg) {
  width: 14px;
  height: 14px;
}
.local-runtime-diag {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-top: 2px;
  font-size: 11px;
  color: var(--accent-warning);
}
.local-runtime-caps {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  margin-top: 4px;
}
</style>
