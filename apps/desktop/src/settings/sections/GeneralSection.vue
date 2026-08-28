<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Radar, RefreshCw, Save, ShieldCheck } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiLabel } from '@/components/ui'
import { api } from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import {
  getDispatchPref,
  getMeetingModelPref,
  setDispatchPref,
  setMeetingModelPref,
  type DispatchPref,
} from '@/lib/dispatchPref'

/**
 * General section extracted from SettingsPage (D7.2).
 *
 * Owns: Gateway connection config (Electron appConfig IPC), dispatch
 * behavior preferences (localStorage via dispatchPref). Workspace defaults
 * remain Core-owned and are managed from Agent Center.
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
const meetingModelDraft = ref<string>(getMeetingModelPref())

async function loadAppConfig(): Promise<void> {
  appConfig.value = await window.tinadec.getAppConfig()
  gatewayUrlDraft.value = appConfig.value.gateway_url
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

const discoveredServices = ref<DiscoveredService[]>([])
const scanState = ref<'idle' | 'scanning' | 'scanned' | 'failed'>('idle')

async function scanServices(): Promise<void> {
  scanState.value = 'scanning'
  try {
    discoveredServices.value = await window.tinadec.discoverServices()
    scanState.value = 'scanned'
    if (discoveredServices.value.length === 0) {
      notify.info({ message: t('settings.serviceDiscoveryEmpty'), source: 'gateway' })
    }
  } catch (error) {
    scanState.value = 'failed'
    discoveredServices.value = []
    status.error({
      key: 'gateway-config',
      source: 'gateway',
      message: error instanceof Error ? error.message : t('settings.serviceDiscoveryFailed'),
    })
  }
}

function selectDiscoveredService(service: DiscoveredService): void {
  if (service.service !== 'gateway' || appConfig.value.managed) return
  gatewayUrlDraft.value = service.url
  notify.success({ message: t('settings.serviceDiscoverySelected'), source: 'gateway' })
}

function onEnterPrefChange(e: Event): void {
  const v = (e.target as HTMLSelectElement).value as DispatchPref
  enterPrefDraft.value = v
  setDispatchPref(v)
}

function onMeetingModelChange(v: string): void {
  meetingModelDraft.value = v
  setMeetingModelPref(v)
}

onMounted(() => {
  void loadAppConfig()
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

      <div class="gateway-config-field service-discovery-field">
        <div class="service-discovery-heading">
          <div>
            <UiLabel>{{ t('settings.serviceDiscovery') }}</UiLabel>
            <div class="gateway-config-meta">
              <span>{{ t('settings.serviceDiscoveryHint') }}</span>
            </div>
          </div>
          <UiButton
            variant="outline"
            :disabled="scanState === 'scanning' || appConfig.managed"
            @click="scanServices"
          >
            <Radar :size="14" :class="{ spinning: scanState === 'scanning' }" />
            {{ scanState === 'scanning' ? t('settings.scanningServices') : t('settings.scanServices') }}
          </UiButton>
        </div>

        <p v-if="appConfig.managed" class="gateway-config-managed">
          <ShieldCheck :size="14" />
          {{ t('settings.gatewayManaged') }}
        </p>
        <ul v-else-if="discoveredServices.length > 0" class="service-discovery-list">
          <li v-for="service in discoveredServices" :key="service.url">
            <button
              v-if="service.service === 'gateway'"
              type="button"
              class="service-discovery-row"
              :class="{ selected: gatewayUrlDraft.trim().replace(/\/$/, '') === service.url }"
              @click="selectDiscoveredService(service)"
            >
              <UiBadge variant="secondary">{{ t('settings.serviceBadgeGateway') }}</UiBadge>
              <span class="service-discovery-url">{{ service.url }}</span>
              <UiBadge :variant="service.core_status === 'ready' ? 'outline' : 'destructive'">
                {{ service.core_status === 'ready' ? t('settings.serviceCoreReady') : t('settings.serviceCoreUnreachable') }}
              </UiBadge>
              <UiBadge v-if="service.current" variant="outline">{{ t('settings.serviceCurrent') }}</UiBadge>
              <span v-if="service.mode" class="service-discovery-meta">{{ service.mode }}</span>
            </button>
            <div v-else class="service-discovery-row service-discovery-row-core">
              <UiBadge variant="outline">{{ t('settings.serviceBadgeCore') }}</UiBadge>
              <span class="service-discovery-url">{{ service.url }}</span>
              <span class="service-discovery-meta">{{ t('settings.serviceCoreDirectHint') }}</span>
            </div>
          </li>
        </ul>
        <div v-else-if="scanState === 'scanned'" class="gateway-config-meta">
          <span>{{ t('settings.serviceDiscoveryEmpty') }}</span>
        </div>
        <div v-else-if="scanState !== 'scanning'" class="gateway-config-meta">
          <span>{{ t('settings.serviceDiscoveryIdleHint') }}</span>
        </div>
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
