<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Radar, RefreshCw, Save, ShieldCheck } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiLabel, UiSelectField } from '@/components/ui'
import { api as baseApi } from '@/api'
import { scopedApi } from '@/lib/storageScope'
const api = scopedApi(baseApi, () => 'user')
import { useNotifications } from '@/composables/useNotifications'
import { getDispatchPref, setDispatchPref, type DispatchPref } from '@/lib/dispatchPref'
import { SUPPORTED_LOCALES, type SupportedLocale } from '@/i18n'

/**
 * General section extracted from SettingsPage (D7.2).
 *
 * Owns: Gateway connection config (Electron appConfig IPC), dispatch
 * behavior preferences (localStorage via dispatchPref), and the interface
 * language (localStorage `tinadec-locale`, the key i18n.ts reads on boot).
 * Workspace defaults remain Core-owned and are managed from Agent Center.
 */
const { t, locale } = useI18n()
const { items: notificationItems, notify, banner, status, confirm: dismissConfirm, dismiss: dismissNotification, dismissByKey } =
  useNotifications()

function setLocale(lang: SupportedLocale): void {
  locale.value = lang
  localStorage.setItem('tinadec-locale', lang)
}

/** Each language is named in itself, so a reader finds theirs whatever the interface is in now. */
const languageOptions = SUPPORTED_LOCALES.map((code) => ({ code, label: t('common.languageName', {}, { locale: code }) }))

interface DesktopAppConfig {
  path?: string
  gateway_url: string
  source: string
  managed: boolean
}

const appConfig = ref<DesktopAppConfig>({ gateway_url: api.gatewayUrl, source: 'default', managed: false })
const gatewayUrlDraft = ref(api.gatewayUrl)
const gatewayConfigBusy = ref(false)
const gatewayConnectionState = ref<'idle' | 'testing' | 'ready' | 'failed'>('idle')
const enterPrefDraft = ref<DispatchPref>(getDispatchPref())

async function loadAppConfig(): Promise<void> {
  appConfig.value = await window.tinadec.getAppConfig()
  gatewayUrlDraft.value = appConfig.value.gateway_url
}

/** The same rules the main process enforces, worded in the interface language before the IPC call. */
function normalizedGatewayDraft(): string {
  let url: URL
  try { url = new URL(gatewayUrlDraft.value.trim()) } catch { throw new Error(t('settings.gatewayUrlInvalid')) }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new Error(t('settings.gatewayUrlInvalid'))
  if (url.username || url.password || url.search || url.hash) throw new Error(t('settings.gatewayUrlNoExtras'))
  return url.toString().replace(/\/$/, '')
}

async function testGatewayConnection(): Promise<void> {
  dismissByKey('gateway-config')
  gatewayConnectionState.value = 'testing'
  let gatewayUrl: string
  try {
    gatewayUrl = normalizedGatewayDraft()
  } catch (error) {
    gatewayConnectionState.value = 'failed'
    status.error({ key: 'gateway-config', source: 'gateway', message: error instanceof Error ? error.message : t('settings.gatewayUrlInvalid') })
    return
  }
  try {
    const response = await fetch(`${gatewayUrl}/api/v1/health`, {
      headers: { accept: 'application/json', 'x-tinadec-storage-id': 'user' },
      signal: AbortSignal.timeout(5000),
    })
    if (!response.ok) throw new Error(t('settings.gatewayConnectionHttp', { status: response.status }))
    gatewayConnectionState.value = 'ready'
    notify.success({ message: t('settings.gatewayConnectionReady'), source: 'gateway' })
  } catch (error) {
    // The browser's own wording ("Failed to fetch", a timeout) stays as detail under a translated message.
    gatewayConnectionState.value = 'failed'
    status.error({ key: 'gateway-config', source: 'gateway', message: t('settings.gatewayConnectionFailed'), details: error instanceof Error ? error.message : undefined })
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

function onEnterPrefChange(value: string): void {
  enterPrefDraft.value = value as DispatchPref
  setDispatchPref(value as DispatchPref)
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
              <span v-if="service.mode" class="service-discovery-meta">{{ t(`settings.serviceMode.${service.mode}`, service.mode) }}</span>
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
          <code v-if="appConfig.path">{{ appConfig.path }}</code>
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
        <UiLabel for="enter-pref">{{ t('settings.enterKeyBehavior') }}</UiLabel>
        <UiSelectField
          id="enter-pref"
          :value="enterPrefDraft"
          :options="[
            { value: 'queued', label: t('settings.enterQueued') },
            { value: 'parallel', label: t('settings.enterParallel') },
            { value: 'ask', label: t('settings.enterAsk') },
          ]"
          @change="onEnterPrefChange"
        />
        <div class="gateway-config-meta">
          <span>{{ t('settings.enterKeyBehaviorMeta') }}</span>
        </div>
      </div>
    </section>

    <section class="general-settings-group" aria-labelledby="language-settings-title">
      <div class="general-settings-group-heading">
        <div>
          <h3 id="language-settings-title">{{ t('settings.language') }}</h3>
        </div>
      </div>

      <div class="lang-options gateway-config-field" data-testid="language-options">
        <UiButton
          v-for="option in languageOptions"
          :key="option.code"
          variant="outline"
          :class="['lang-option', { active: locale === option.code }]"
          :lang="option.code"
          :aria-pressed="locale === option.code"
          :data-testid="`lang-${option.code}`"
          @click="setLocale(option.code)"
        >
          {{ option.label }}
        </UiButton>
      </div>
    </section>
  </div>
</template>
