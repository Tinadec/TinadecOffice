<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RefreshCw, Save, ShieldCheck } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiLabel } from '@/components/ui'
import { api, type AgentModeTopologyDto } from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import {
  getDispatchPref,
  getMeetingModelPref,
  getModeVersionPref,
  setDispatchPref,
  setMeetingModelPref,
  setModeVersionPref,
  type DispatchPref,
} from '@/lib/dispatchPref'

/**
 * General section extracted from SettingsPage (D7.2).
 *
 * Owns: Gateway connection config (Electron appConfig IPC), dispatch
 * behavior preferences (localStorage via dispatchPref), and the default
 * mode-topology picker. No shared state with other settings sections.
 */
const { t } = useI18n()
const { items: notificationItems, notify, banner, status, confirm: dismissConfirm, dismiss: dismissNotification, dismissByKey } =
  useNotifications()

interface DesktopAppConfig {
  gateway_url: string
  source: string
  managed: boolean
}

const appConfig = ref<DesktopAppConfig>({ gateway_url: api.gatewayUrl, source: 'default', managed: false })
const gatewayUrlDraft = ref(api.gatewayUrl)
const gatewayConfigBusy = ref(false)
const gatewayConnectionState = ref<'idle' | 'testing' | 'ready' | 'failed'>('idle')
const enterPrefDraft = ref<DispatchPref>(getDispatchPref())
const modeVersionDraft = ref<string | null>(getModeVersionPref())
const meetingModelDraft = ref<string>(getMeetingModelPref())
const generalTopologies = ref<AgentModeTopologyDto[]>([])
const generalTopologiesLoading = ref(false)

async function loadAppConfig(): Promise<void> {
  appConfig.value = await window.tinadec.getAppConfig()
  gatewayUrlDraft.value = appConfig.value.gateway_url
}

async function loadGeneralTopologies(): Promise<void> {
  generalTopologiesLoading.value = true
  try {
    const list = await api.listAgentModeTopologies()
    generalTopologies.value = Array.isArray(list) ? (list as AgentModeTopologyDto[]) : []
  } catch {
    /* ignore offline */
  } finally {
    generalTopologiesLoading.value = false
  }
}

function normalizedGatewayDraft(): string {
  const url = new URL(gatewayUrlDraft.value.trim())
  if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new Error(t('settings.gatewayUrlInvalid'))
  return url.toString().replace(/\/$/, '')
}

async function testGatewayConnection(): Promise<void> {
  dismissByKey('gateway-config')
  gatewayConnectionState.value = 'testing'
  try {
    const gatewayUrl = normalizedGatewayDraft()
    const response = await fetch(`${gatewayUrl}/api/v1/health`, {
      headers: { accept: 'application/json' },
      signal: AbortSignal.timeout(5000),
    })
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`)
    gatewayConnectionState.value = 'ready'
    notify.success({ message: t('settings.gatewayConnectionReady'), source: 'gateway' })
  } catch (error) {
    gatewayConnectionState.value = 'failed'
    status.error({ key: 'gateway-config', source: 'gateway', message: error instanceof Error ? error.message : t('settings.gatewayConnectionFailed') })
  }
}

async function saveGatewayConfiguration(): Promise<void> {
  dismissByKey('gateway-config')
  try {
    normalizedGatewayDraft()
  } catch (error) {
    status.error({ key: 'gateway-config', source: 'gateway', message: error instanceof Error ? error.message : t('settings.gatewayUrlInvalid') })
    return
  }
  gatewayConfigBusy.value = true
  dismissByKey('gateway-config')
  try {
    appConfig.value = await window.tinadec.saveGatewayUrl(gatewayUrlDraft.value)
    gatewayUrlDraft.value = appConfig.value.gateway_url
    if (appConfig.value.gateway_url !== api.gatewayUrl) {
      banner.warning({
        key: 'gateway-restart',
        message: t('settings.gatewaySavedRestart'),
        action: { label: t('settings.restartNow'), run: restartDesktop },
      })
    } else {
      clearGatewayRestartBanner()
      notify.success(t('settings.gatewaySaved'))
    }
  } catch (error) {
    notify.error(error, { title: t('settings.gatewaySaveFailed') })
  } finally {
    gatewayConfigBusy.value = false
  }
}

async function resetGatewayConfiguration(): Promise<void> {
  gatewayConfigBusy.value = true
  dismissByKey('gateway-config')
  try {
    appConfig.value = await window.tinadec.resetGatewayUrl()
    gatewayUrlDraft.value = appConfig.value.gateway_url
    gatewayConnectionState.value = 'idle'
    if (appConfig.value.gateway_url !== api.gatewayUrl) {
      banner.warning({
        key: 'gateway-restart',
        message: t('settings.gatewayResetRestart'),
        action: { label: t('settings.restartNow'), run: restartDesktop },
      })
    } else {
      clearGatewayRestartBanner()
      notify.success(t('settings.gatewayReset'))
    }
  } catch (error) {
    notify.error(error, { title: t('settings.gatewaySaveFailed') })
  } finally {
    gatewayConfigBusy.value = false
  }
}

function restartDesktop(): void {
  void window.tinadec.restartApp()
}

function clearGatewayRestartBanner(): void {
  const existing = notificationItems.value.find((item) => item.key === 'gateway-restart')
  if (existing) dismissNotification(existing.id)
}

function onEnterPrefChange(e: Event): void {
  const v = (e.target as HTMLSelectElement).value as DispatchPref
  enterPrefDraft.value = v
  setDispatchPref(v)
}

function onModeVersionChange(e: Event): void {
  const v = (e.target as HTMLSelectElement).value || null
  modeVersionDraft.value = v
  setModeVersionPref(v)
}

function onMeetingModelChange(v: string): void {
  meetingModelDraft.value = v
  setMeetingModelPref(v)
}

onMounted(() => {
  void loadAppConfig()
  void loadGeneralTopologies()
})

// Re-exported for template type inference only.
void dismissConfirm
</script>

<template>
  <div>
    <div class="general-settings-heading">
      <div>
        <h2>{{ t('settings.general') }}</h2>
        <p>{{ t('settings.generalSubtitle') }}</p>
      </div>
    </div>

    <section class="general-settings-group" aria-labelledby="gateway-settings-title">
      <div class="general-settings-group-heading">
        <div>
          <h3 id="gateway-settings-title">{{ t('settings.gatewayConnection') }}</h3>
          <p>{{ t('settings.gatewayConnectionHint') }}</p>
        </div>
        <UiBadge :variant="gatewayConnectionState === 'ready' ? 'secondary' : gatewayConnectionState === 'failed' ? 'destructive' : 'outline'">
          {{ gatewayConnectionState === 'testing'
            ? t('settings.gatewayTesting')
            : gatewayConnectionState === 'ready'
              ? t('settings.gatewayConnected')
              : gatewayConnectionState === 'failed'
                ? t('settings.gatewayUnreachable')
                : t('settings.gatewayNotTested') }}
        </UiBadge>
      </div>

      <div class="gateway-config-field">
        <UiLabel for="gateway-url">{{ t('settings.gatewayUrl') }}</UiLabel>
        <UiInput
          id="gateway-url"
          v-model="gatewayUrlDraft"
          type="url"
          :disabled="appConfig.managed || gatewayConfigBusy"
          placeholder="https://tinadec.example.com"
          @keydown.enter="testGatewayConnection"
        />
        <div class="gateway-config-meta">
          <span>{{ t('settings.gatewayConfigSource') }}: {{ t(`settings.gatewaySource_${appConfig.source}`) }}</span>
          <span>{{ t('settings.gatewayHttpsHint') }}</span>
        </div>
      </div>

      <p v-if="appConfig.managed" class="gateway-config-managed">
        <ShieldCheck :size="14" />
        {{ t('settings.gatewayManaged') }}
      </p>

      <div class="gateway-config-actions">
        <UiButton variant="outline" :disabled="gatewayConnectionState === 'testing'" @click="testGatewayConnection">
          <RefreshCw :size="14" :class="{ spinning: gatewayConnectionState === 'testing' }" />
          {{ t('settings.testConnection') }}
        </UiButton>
        <UiButton variant="outline" :disabled="appConfig.managed || gatewayConfigBusy" @click="resetGatewayConfiguration">
          {{ t('settings.restoreDefault') }}
        </UiButton>
        <UiButton :disabled="appConfig.managed || gatewayConfigBusy" @click="saveGatewayConfiguration">
          <Save :size="14" />
          {{ t('settings.save') }}
        </UiButton>
      </div>
    </section>

    <section class="general-settings-group" aria-labelledby="dispatch-settings-title">
      <div class="general-settings-group-heading">
        <div>
          <h3 id="dispatch-settings-title">{{ t('settings.dispatchBehavior') }}</h3>
          <p>{{ t('settings.dispatchBehaviorHint') }}</p>
        </div>
      </div>

      <div class="gateway-config-field">
        <UiLabel for="mode-version-pref">{{ t('settings.defaultModeTopology') }}</UiLabel>
        <select
          id="mode-version-pref"
          class="settings-select"
          :value="modeVersionDraft ?? ''"
          :disabled="generalTopologiesLoading && generalTopologies.length === 0"
          @change="onModeVersionChange"
        >
          <option value="">{{ t('settings.defaultModeTopologyFollow') }}</option>
          <option v-for="m in generalTopologies" :key="m.id" :value="m.id">
            {{ m.display_name }}{{ m.status === 'published' ? ' · 默认' : '' }}
          </option>
        </select>
        <div class="gateway-config-meta">
          <span>{{ t('settings.defaultModeTopologyHint') }}</span>
        </div>
      </div>

      <div class="gateway-config-field">
        <UiLabel for="meeting-model-pref">{{ t('settings.defaultMeetingModel') }}</UiLabel>
        <UiInput
          id="meeting-model-pref"
          :model-value="meetingModelDraft"
          :placeholder="t('settings.defaultMeetingModelPlaceholder')"
          class="settings-input"
          @update:model-value="onMeetingModelChange"
        />
        <div class="gateway-config-meta">
          <span>{{ t('settings.defaultMeetingModelHint') }}</span>
        </div>
      </div>

      <div class="gateway-config-field">
        <UiLabel for="enter-pref">{{ t('settings.enterKeyBehavior') }}</UiLabel>
        <select id="enter-pref" class="settings-select" :value="enterPrefDraft" @change="onEnterPrefChange">
          <option value="queued">{{ t('settings.enterQueued') }}</option>
          <option value="parallel">{{ t('settings.enterParallel') }}</option>
          <option value="ask">{{ t('settings.enterAsk') }}</option>
        </select>
        <div class="gateway-config-meta">
          <span>{{ t('settings.enterKeyBehaviorMeta') }}</span>
        </div>
      </div>
    </section>
  </div>
</template>
