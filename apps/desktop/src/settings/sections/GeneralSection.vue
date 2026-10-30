<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Radar, RefreshCw, Save, ShieldCheck } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiLabel, UiSelectField, UiSwitch } from '@/components/ui'
import { api as baseApi } from '@/api'
import { scopedApi } from '@/lib/storageScope'
const api = scopedApi(baseApi, () => 'user')
import { useNotifications } from '@/composables/useNotifications'
import { getDispatchPref, setDispatchPref, type DispatchPref } from '@/lib/dispatchPref'
import {
  CONVERSATION_FLOW_PRESETS,
  conversationFlowPreset,
  setConversationFlowPreset,
  type ConversationFlowPreset,
} from '@/lib/conversationFlowStyle'
import { SUPPORTED_LOCALES, setInterfaceLocale, type SupportedLocale } from '@/i18n'

/**
 * General section extracted from SettingsPage (D7.2).
 *
 * Owns: Gateway connection config (Electron appConfig IPC), dispatch
 * behavior preferences (localStorage via dispatchPref), the interface
 * language (localStorage `tinadec-locale`, the key i18n.ts reads on boot), and
 * the window/tray preference in desktop.toml.
 * Workspace defaults remain Core-owned and are managed from Agent Center.
 */
const { t, locale } = useI18n()
const { items: notificationItems, notify, banner, status, confirm: dismissConfirm, dismiss: dismissNotification, dismissByKey } =
  useNotifications()

/**
 * Switch the interface language. Every non-default bundle is a separate chunk now, so the switch waits
 * for it: setting `locale` first would repaint the window in a language whose bundle is still in flight.
 * The stored key is written only after the bundle arrives, so another window never switches to a
 * language this one could not load either.
 */
async function setLocale(lang: SupportedLocale): Promise<void> {
  if (!(await setInterfaceLocale(lang))) return
  localStorage.setItem('tinadec-locale', lang)
}

/** Each language is named in itself, so a reader finds theirs whatever the interface is in now. */
const languageOptions = SUPPORTED_LOCALES.map((code) => ({ code, label: t('common.languageName', {}, { locale: code }) }))

interface DesktopAppConfig {
  path?: string
  gateway_url: string
  source: string
  managed: boolean
  close_to_tray?: boolean
  companion_mode_enabled?: boolean
  companion_capsule_style?: 'peek' | 'grip'
  companion_capsule_reveal?: number
}

const appConfig = ref<DesktopAppConfig>({ gateway_url: api.gatewayUrl, source: 'default', managed: false })
const gatewayUrlDraft = ref(api.gatewayUrl)
const gatewayConfigBusy = ref(false)
const gatewayConnectionState = ref<'idle' | 'testing' | 'ready' | 'failed'>('idle')
const enterPrefDraft = ref<DispatchPref>(getDispatchPref())

/** desktop.toml `[window] close_to_tray`: closing the main window hides it to the tray. */
const closeToTray = ref(false)
const closeToTrayBusy = ref(false)

/** desktop.toml `[companion] enabled`: the docked capsule that expands into a panel. */
const companionMode = ref(false)
const companionModeBusy = ref(false)

/**
 * desktop.toml `[companion] capsule_style`: whether the collapsed capsule shows the pet's head
 * peeking out of the screen edge, or a bare handle. There is no width setting: each style has the
 * width its drawing needs — one head for the peek, a sliver for the grip.
 */
const CAPSULE_STYLE_OPTIONS = ['peek', 'grip'] as const
type CapsuleStyle = (typeof CAPSULE_STYLE_OPTIONS)[number]
const capsuleStyle = ref<CapsuleStyle>('peek')
const capsuleStyleBusy = ref(false)

/**
 * desktop.toml `[companion] capsule_reveal`: how much of the peeking pet stays on screen.
 *
 * Not the same axis as a width would be: the pet is drawn at one size either way, and this only
 * parks the window further out (10% — a sliver of shoulder) or further in (100% — the whole pet).
 */
const CAPSULE_REVEAL_OPTIONS = [10, 50, 100] as const
const capsuleReveal = ref<number>(50)
const capsuleRevealBusy = ref(false)

/** Explicit keys rather than a template string, so every label is a literal a scan can find. */
function capsuleStyleLabel(style: CapsuleStyle): string {
  return style === 'peek'
    ? t('settings.companionCapsuleStylePeek')
    : t('settings.companionCapsuleStyleGrip')
}

async function loadAppConfig(): Promise<void> {
  appConfig.value = await window.tinadec.getAppConfig()
  gatewayUrlDraft.value = appConfig.value.gateway_url
  closeToTray.value = appConfig.value.close_to_tray === true
  companionMode.value = appConfig.value.companion_mode_enabled === true
  capsuleStyle.value = appConfig.value.companion_capsule_style ?? 'peek'
  capsuleReveal.value = appConfig.value.companion_capsule_reveal ?? 50
}

/**
 * The tray checkbox writes the same preference, so a change made there arrives as a broadcast while
 * this section is mounted. Both sides read the stored value back instead of assuming the write won.
 */
async function setCloseToTray(enabled: boolean): Promise<void> {
  if (closeToTrayBusy.value) return
  const previous = closeToTray.value
  closeToTray.value = enabled
  closeToTrayBusy.value = true
  try {
    closeToTray.value = (await window.tinadec.saveCloseToTray(enabled)).close_to_tray === true
  } catch (error) {
    closeToTray.value = previous
    notify.error(error, { title: t('settings.closeToTraySaveFailed') })
  } finally {
    closeToTrayBusy.value = false
  }
}

/**
 * The flag is stored first and the window is created from it, so a failure to show the window
 * arrives as a rejected call instead of a stored preference that silently does nothing.
 */
async function setCompanionMode(enabled: boolean): Promise<void> {
  if (companionModeBusy.value) return
  const previous = companionMode.value
  companionMode.value = enabled
  companionModeBusy.value = true
  try {
    companionMode.value = (await window.tinadec.saveCompanionMode(enabled)).companion_mode_enabled === true
  } catch (error) {
    companionMode.value = previous
    notify.error(error, { title: t('settings.companionModeSaveFailed') })
  } finally {
    companionModeBusy.value = false
  }
}

/**
 * The stored style and the live capsule are updated by the main process in one call, so a write
 * that fails never leaves this section showing a style the capsule is not drawing.
 */
async function setCapsuleStyle(style: CapsuleStyle): Promise<void> {
  if (capsuleStyleBusy.value || capsuleStyle.value === style) return
  const previous = capsuleStyle.value
  capsuleStyle.value = style
  capsuleStyleBusy.value = true
  try {
    capsuleStyle.value = (await window.tinadec.saveCompanionCapsuleStyle(style)).companion_capsule_style as CapsuleStyle
  } catch (error) {
    capsuleStyle.value = previous
    notify.error(error, { title: t('settings.companionCapsuleStyleSaveFailed') })
  } finally {
    capsuleStyleBusy.value = false
  }
}

/** Same contract as the style: the stored step and the live capsule move together in one call. */
async function setCapsuleReveal(reveal: number): Promise<void> {
  if (capsuleRevealBusy.value || capsuleReveal.value === reveal) return
  const previous = capsuleReveal.value
  capsuleReveal.value = reveal
  capsuleRevealBusy.value = true
  try {
    capsuleReveal.value = (await window.tinadec.saveCompanionCapsuleReveal(reveal)).companion_capsule_reveal
  } catch (error) {
    capsuleReveal.value = previous
    notify.error(error, { title: t('settings.companionCapsuleRevealSaveFailed') })
  } finally {
    capsuleRevealBusy.value = false
  }
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

/**
 * How much of a run's work the conversation shows, as one five-step ladder. The control is a
 * segmented switch of density words, and the line underneath describes only the chosen step:
 * five parallel descriptions read as five recommendations rather than one setting.
 */
const flowStyleOptions = CONVERSATION_FLOW_PRESETS.map((entry) => entry.preset)
const flowStyleDescription = computed(() => t(`settings.conversationFlowPresets.${conversationFlowPreset.value}.summary`))

function chooseFlowStyle(next: ConversationFlowPreset): void {
  setConversationFlowPreset(next)
}

/** Segmented keys: the arrows move the choice and the focus together, as a radio group does. */
function onFlowStyleKeydown(event: KeyboardEvent, index: number): void {
  const step = event.key === 'ArrowDown' || event.key === 'ArrowRight' ? 1
    : event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? -1
      : 0
  if (!step) return
  event.preventDefault()
  const next = (index + step + flowStyleOptions.length) % flowStyleOptions.length
  chooseFlowStyle(flowStyleOptions[next]!)
  const radios = (event.currentTarget as HTMLElement | null)?.parentElement?.querySelectorAll<HTMLElement>('[role="radio"]')
  radios?.[next]?.focus()
}

let disposeCloseToTray: (() => void) | undefined
onMounted(() => {
  void loadAppConfig()
  disposeCloseToTray = window.tinadec.onCloseToTrayChanged(({ close_to_tray }) => {
    closeToTray.value = close_to_tray === true
  })
})
onUnmounted(() => {
  disposeCloseToTray?.()
  disposeCloseToTray = undefined
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

    <section class="general-settings-group" aria-labelledby="conversation-flow-title">
      <div class="general-settings-group-heading">
        <div>
          <h3 id="conversation-flow-title">{{ t('settings.conversationFlow') }}</h3>
          <p>{{ t('settings.conversationFlowHint') }}</p>
        </div>
      </div>

      <div
        class="flow-style-switch gateway-config-field"
        role="radiogroup"
        aria-orientation="horizontal"
        :aria-label="t('settings.conversationFlow')"
        aria-describedby="flow-style-description"
        data-testid="conversation-flow-options"
      >
        <button
          v-for="(preset, index) in flowStyleOptions"
          :key="preset"
          type="button"
          role="radio"
          class="flow-style-segment"
          :class="{ active: conversationFlowPreset === preset }"
          :aria-checked="conversationFlowPreset === preset"
          :tabindex="conversationFlowPreset === preset ? 0 : -1"
          :data-testid="`flow-style-${preset}`"
          @click="chooseFlowStyle(preset)"
          @keydown="onFlowStyleKeydown($event, index)"
        >{{ t(`settings.conversationFlowPresets.${preset}.label`) }}</button>
      </div>
      <p id="flow-style-description" class="flow-style-description" data-testid="flow-style-description">
        {{ flowStyleDescription }}
      </p>
    </section>

    <section class="general-settings-group" aria-labelledby="window-settings-title">
      <div class="general-settings-group-heading">
        <div>
          <h3 id="window-settings-title">{{ t('settings.windowAndTray') }}</h3>
          <p>{{ t('settings.windowAndTrayHint') }}</p>
        </div>
      </div>

      <div class="gateway-config-field">
        <div class="setting-switch-row">
          <div>
            <UiLabel>{{ t('settings.closeToTray') }}</UiLabel>
            <div class="gateway-config-meta">
              <span>{{ t('settings.closeToTrayHint') }}</span>
            </div>
          </div>
          <UiSwitch
            id="close-to-tray"
            :model-value="closeToTray"
            :disabled="closeToTrayBusy"
            :aria-label="t('settings.closeToTray')"
            data-testid="close-to-tray-switch"
            @update:model-value="setCloseToTray"
          />
        </div>
      </div>
    </section>

    <section class="general-settings-group" aria-labelledby="companion-settings-title">
      <div class="general-settings-group-heading">
        <div>
          <h3 id="companion-settings-title">{{ t('settings.companionMode') }}</h3>
          <p>{{ t('settings.companionModeHint') }}</p>
        </div>
      </div>

      <div class="gateway-config-field">
        <div class="setting-switch-row">
          <div>
            <UiLabel>{{ t('settings.companionModeEnable') }}</UiLabel>
            <div class="gateway-config-meta">
              <span>{{ t('settings.companionModeEnableHint') }}</span>
            </div>
          </div>
          <UiSwitch
            id="companion-mode"
            :model-value="companionMode"
            :disabled="companionModeBusy"
            :aria-label="t('settings.companionModeEnable')"
            data-testid="companion-mode-switch"
            @update:model-value="setCompanionMode"
          />
        </div>
      </div>

      <div class="gateway-config-field">
        <UiLabel>{{ t('settings.companionCapsuleStyle') }}</UiLabel>
        <div
          class="companion-choice-options"
          role="radiogroup"
          :aria-label="t('settings.companionCapsuleStyle')"
          data-testid="companion-style-options"
        >
          <button
            v-for="option in CAPSULE_STYLE_OPTIONS"
            :key="option"
            type="button"
            role="radio"
            class="companion-choice-option"
            :class="{ active: capsuleStyle === option }"
            :aria-checked="capsuleStyle === option"
            :disabled="capsuleStyleBusy"
            :data-testid="`companion-style-${option}`"
            @click="setCapsuleStyle(option)"
          >
            <span class="companion-style-preview" :class="`is-${option}`" aria-hidden="true" />
            <span class="companion-choice-label">{{ capsuleStyleLabel(option) }}</span>
          </button>
        </div>
        <div class="gateway-config-meta">
          <span>{{ t('settings.companionCapsuleStyleHint') }}</span>
        </div>
      </div>

      <!-- Only the peek has a figure to reveal; a grip is a handle with nothing behind it. -->
      <div v-if="capsuleStyle === 'peek'" class="gateway-config-field">
        <UiLabel>{{ t('settings.companionCapsuleReveal') }}</UiLabel>
        <div
          class="companion-choice-options"
          role="radiogroup"
          :aria-label="t('settings.companionCapsuleReveal')"
          data-testid="companion-reveal-options"
        >
          <button
            v-for="option in CAPSULE_REVEAL_OPTIONS"
            :key="option"
            type="button"
            role="radio"
            class="companion-choice-option"
            :class="{ active: capsuleReveal === option }"
            :aria-checked="capsuleReveal === option"
            :disabled="capsuleRevealBusy"
            :data-testid="`companion-reveal-${option}`"
            @click="setCapsuleReveal(option)"
          >
            <span class="companion-reveal-preview" aria-hidden="true">
              <span :style="{ width: `${option}%` }" />
            </span>
            <span class="companion-choice-label">{{ option }}%</span>
          </button>
        </div>
        <div class="gateway-config-meta">
          <span>{{ t('settings.companionCapsuleRevealHint') }}</span>
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
