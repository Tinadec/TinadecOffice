<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
} from 'reka-ui'
import { Check, Copy, LoaderCircle, Radar, ShieldCheck, X } from '@lucide/vue'
import { UiBadge, UiButton, UiCheckbox, UiInput, UiLabel, UiSwitch } from '@/components/ui'
import {
  formatPeerFingerprint,
  usePeerDevices,
  type PeerDeviceDto,
  type PeerProbeResultDto,
} from '@/composables/usePeerDevices'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { useNotifications } from '@/composables/useNotifications'
import { errorCodeMessage } from '@/lib/apiError'

const props = defineProps<{
  open: boolean
}>()

const emit = defineEmits<{
  'update:open': [value: boolean]
  paired: [device: PeerDeviceDto]
}>()

const { t, te } = useI18n()
const { getPanelDataAttributes } = usePanelStyles()
const { notify } = useNotifications()
const { probeDevice, pairDevice } = usePeerDevices()

const address = ref('')
const pairingCodeInput = ref('')
const customDisplayName = ref('')
const connectionDesired = ref(true)
const adoptSessions = ref(true)

const probing = ref(false)
const pairing = ref(false)
const probeResult = ref<PeerProbeResultDto | null>(null)
const probedAddress = ref('')
const fingerprintConfirmed = ref(false)
const copiedFingerprint = ref(false)
const inlineError = ref<string | null>(null)

const busy = computed(() => probing.value || pairing.value)
const addressChangedSinceProbe = computed(
  () => Boolean(probedAddress.value && address.value.trim() !== probedAddress.value),
)

const formattedFingerprintBlocks = computed(() => {
  if (!probeResult.value?.certificate_fingerprint) return []
  return formatPeerFingerprint(probeResult.value.certificate_fingerprint)
    .split(/\s+/)
    .filter(Boolean)
})

const canProbe = computed(() => Boolean(address.value.trim()) && !busy.value)
const canPair = computed(
  () =>
    Boolean(probeResult.value?.certificate_fingerprint)
    && !addressChangedSinceProbe.value
    && fingerprintConfirmed.value
    && pairingCodeInput.value.trim().length >= 6
    && !busy.value,
)

function resetState() {
  address.value = ''
  pairingCodeInput.value = ''
  customDisplayName.value = ''
  connectionDesired.value = true
  adoptSessions.value = true
  probing.value = false
  pairing.value = false
  probeResult.value = null
  probedAddress.value = ''
  fingerprintConfirmed.value = false
  copiedFingerprint.value = false
  inlineError.value = null
}

watch(
  () => props.open,
  (isOpen) => {
    if (isOpen) resetState()
  },
)

watch(address, (next) => {
  inlineError.value = null
  if (probedAddress.value && next.trim() !== probedAddress.value) {
    probeResult.value = null
    fingerprintConfirmed.value = false
  }
})

function formatError(err: unknown): string {
  const code = (err as { code?: string | null })?.code
  const mapped = errorCodeMessage(code, undefined, { t, te })
  if (mapped) return mapped
  return err instanceof Error ? err.message : String(err)
}

function handleOpenChange(next: boolean) {
  if (busy.value && !next) return
  emit('update:open', next)
}

async function handleProbe() {
  const target = address.value.trim()
  if (!target || busy.value) return
  probing.value = true
  inlineError.value = null
  fingerprintConfirmed.value = false
  try {
    const res = await probeDevice(target)
    probeResult.value = res
    probedAddress.value = target
    if (!customDisplayName.value.trim() && res.display_name) {
      customDisplayName.value = res.display_name
    }
  } catch (err) {
    probeResult.value = null
    inlineError.value = formatError(err)
  } finally {
    probing.value = false
  }
}

async function copyProbedFingerprint() {
  const raw = probeResult.value?.certificate_fingerprint
  if (!raw) return
  try {
    await navigator.clipboard?.writeText(formatPeerFingerprint(raw))
    copiedFingerprint.value = true
    notify.success(t('peers.copiedFingerprint'))
    setTimeout(() => {
      copiedFingerprint.value = false
    }, 1800)
  } catch {
    // Clipboard unavailable in headless environments
  }
}

async function handleConfirmPair() {
  if (!canPair.value || !probeResult.value) return
  pairing.value = true
  inlineError.value = null
  try {
    const device = await pairDevice({
      address: address.value.trim(),
      pairing_code: pairingCodeInput.value.trim(),
      expected_fingerprint: probeResult.value.certificate_fingerprint,
      display_name: customDisplayName.value.trim() || probeResult.value.display_name,
      connection_desired: connectionDesired.value,
      adopt_sessions: adoptSessions.value,
    })
    notify.success(t('peers.pairingModal.pairedSuccess', { name: device.display_name }))
    emit('paired', device)
    emit('update:open', false)
  } catch (err) {
    inlineError.value = formatError(err)
  } finally {
    pairing.value = false
  }
}
</script>

<template>
  <DialogRoot :open="open" @update:open="handleOpenChange">
    <DialogPortal>
      <DialogOverlay class="peer-modal-overlay" />
      <!-- IMPORTANT: Centered via fixed inset + margin:auto without any ancestor or self transform,
           preserving backdrop-filter compositing across all Chromium/Electron material presets. -->
      <DialogContent
        class="peer-modal-surface"
        v-bind="getPanelDataAttributes()"
        data-testid="peer-pairing-modal"
        @escape-key-down="busy && $event.preventDefault()"
        @interact-outside="busy && $event.preventDefault()"
      >
        <header class="peer-modal-header">
          <div class="peer-modal-heading">
            <ShieldCheck :size="18" class="peer-modal-icon" aria-hidden="true" />
            <div>
              <DialogTitle class="peer-modal-title">{{ t('peers.pairingModal.title') }}</DialogTitle>
              <DialogDescription class="peer-modal-desc">{{ t('peers.pairingModal.description') }}</DialogDescription>
            </div>
          </div>
          <DialogClose as-child>
            <UiButton type="button" variant="ghost" size="icon" :aria-label="t('common.close')" :disabled="busy">
              <X :size="16" />
            </UiButton>
          </DialogClose>
        </header>

        <form class="peer-modal-body" @submit.prevent="handleConfirmPair">
          <div class="peer-modal-grid">
            <div class="peer-modal-field">
              <UiLabel for="peer-pair-address">{{ t('peers.pairingModal.addressLabel') }}</UiLabel>
              <div class="peer-modal-inline-input">
                <UiInput
                  id="peer-pair-address"
                  v-model="address"
                  class="font-mono"
                  :disabled="busy"
                  :placeholder="t('peers.pairingModal.addressPlaceholder')"
                  data-testid="peer-pair-address-input"
                />
                <UiButton
                  type="button"
                  variant="outline"
                  size="sm"
                  :disabled="!canProbe"
                  data-testid="peer-probe-btn"
                  @click="handleProbe"
                >
                  <LoaderCircle v-if="probing" :size="14" class="animate-spin" />
                  <Radar v-else :size="14" />
                  <span>{{ probing ? t('peers.pairingModal.probing') : t('peers.pairingModal.probeBtn') }}</span>
                </UiButton>
              </div>
            </div>

            <div class="peer-modal-grid-two">
              <div class="peer-modal-field">
                <UiLabel for="peer-pair-code">{{ t('peers.pairingModal.pairingCodeLabel') }}</UiLabel>
                <UiInput
                  id="peer-pair-code"
                  v-model="pairingCodeInput"
                  class="font-mono"
                  :disabled="busy"
                  :placeholder="t('peers.pairingModal.pairingCodePlaceholder')"
                  autocomplete="one-time-code"
                  data-testid="peer-pair-code-input"
                />
              </div>

              <div class="peer-modal-field">
                <UiLabel for="peer-pair-alias">{{ t('peers.pairingModal.aliasLabel') }}</UiLabel>
                <UiInput
                  id="peer-pair-alias"
                  v-model="customDisplayName"
                  :disabled="busy"
                  :placeholder="t('peers.pairingModal.aliasPlaceholder')"
                />
              </div>
            </div>
          </div>

          <p v-if="addressChangedSinceProbe" class="peer-modal-notice" role="status">
            {{ t('peers.pairingModal.reprobeHint') }}
          </p>

          <!-- Dual-End Juxtaposed Identity + Live TLS SHA-256 Fingerprint Verification Box -->
          <section
            v-if="probeResult && !addressChangedSinceProbe"
            class="peer-verify-box"
            data-testid="peer-fingerprint-verify-box"
          >
            <div class="peer-verify-head">
              <strong>{{ t('peers.pairingModal.verifyBoxTitle') }}</strong>
              <UiBadge variant="outline">
                {{ t('peers.pairingModal.protocolLabel', { major: probeResult.protocol_major, minor: probeResult.protocol_minor }) }}
              </UiBadge>
            </div>

            <div class="peer-verify-juxtaposed">
              <div class="peer-verify-identity">
                <span class="peer-verify-caption">{{ t('peers.pairingModal.remoteNameLabel') }}</span>
                <strong class="peer-verify-device-name" data-testid="peer-probed-device-name">
                  {{ probeResult.display_name }}
                </strong>
                <span class="peer-verify-caption">{{ t('peers.pairingModal.remoteIdLabel') }}</span>
                <code class="peer-verify-device-id">{{ probeResult.remote_device_id }}</code>
              </div>

              <div class="peer-verify-fingerprint">
                <div class="peer-verify-fp-head">
                  <span class="peer-verify-caption">{{ t('peers.pairingModal.liveFingerprintLabel') }}</span>
                  <UiButton
                    type="button"
                    variant="ghost"
                    size="sm"
                    :aria-label="t('peers.inbound.copyFingerprint')"
                    @click="copyProbedFingerprint"
                  >
                    <Check v-if="copiedFingerprint" :size="13" />
                    <Copy v-else :size="13" />
                  </UiButton>
                </div>
                <div class="peer-fingerprint-blocks" data-testid="peer-probed-fingerprint">
                  <code v-for="(chunk, idx) in formattedFingerprintBlocks" :key="idx" class="peer-fp-chunk">
                    {{ chunk }}
                  </code>
                </div>
              </div>
            </div>

            <p class="peer-verify-instruction">{{ t('peers.pairingModal.verifyInstruction') }}</p>

            <div
              class="peer-verify-confirm-row"
              data-testid="peer-fingerprint-confirm-row"
              @click="fingerprintConfirmed = !fingerprintConfirmed"
            >
              <UiCheckbox
                :model-value="fingerprintConfirmed"
                :aria-label="t('peers.pairingModal.confirmCheckbox')"
                data-testid="peer-fingerprint-confirm-checkbox"
                @update:model-value="fingerprintConfirmed = $event"
              />
              <span>{{ t('peers.pairingModal.confirmCheckbox') }}</span>
            </div>
          </section>

          <div class="peer-modal-switches">
            <div class="peer-modal-switch-row">
              <span>{{ t('peers.pairingModal.autoConnectLabel') }}</span>
              <UiSwitch v-model="connectionDesired" :disabled="busy" :aria-label="t('peers.pairingModal.autoConnectLabel')" />
            </div>
            <div class="peer-modal-switch-row">
              <span>{{ t('peers.pairingModal.adoptSessionsLabel') }}</span>
              <UiSwitch v-model="adoptSessions" :disabled="busy" :aria-label="t('peers.pairingModal.adoptSessionsLabel')" />
            </div>
          </div>

          <p v-if="inlineError" class="peer-modal-error" role="alert" data-testid="peer-pair-error">
            {{ inlineError }}
          </p>

          <footer class="peer-modal-footer">
            <UiButton type="button" variant="ghost" size="sm" :disabled="busy" @click="handleOpenChange(false)">
              {{ t('common.cancel') }}
            </UiButton>
            <UiButton
              type="submit"
              size="sm"
              :disabled="!canPair"
              data-testid="peer-confirm-pair-btn"
            >
              <LoaderCircle v-if="pairing" :size="14" class="animate-spin" />
              <ShieldCheck v-else :size="14" />
              <span>{{ pairing ? t('peers.pairingModal.pairing') : t('peers.pairingModal.confirmPairBtn') }}</span>
            </UiButton>
          </footer>
        </form>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>

<style scoped>
.peer-modal-overlay {
  position: fixed;
  inset: 0;
  z-index: 90;
  background: color-mix(in srgb, var(--bg-overlay, #09090b) 62%, transparent);
  transition: opacity 160ms ease;
}

.peer-modal-surface {
  position: fixed;
  inset: 0;
  z-index: 91;
  margin: auto;
  width: min(560px, calc(100vw - 32px));
  height: fit-content;
  max-height: calc(100vh - 48px);
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 20px;
  border-radius: 12px;
  border: 1px solid var(--material-floating-border, var(--border-card));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  color: var(--text-primary);
  box-shadow: var(--shadow-card-subtle);
  transition: opacity 160ms ease;
}

.peer-modal-surface:focus-visible {
  outline: 2px solid var(--focus-ring-color, var(--accent-primary));
  outline-offset: 2px;
}

.peer-modal-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.peer-modal-heading {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  min-width: 0;
}

.peer-modal-icon {
  flex-shrink: 0;
  margin-top: 2px;
  color: var(--text-primary);
}

.peer-modal-title {
  margin: 0;
  font-size: 15px;
  font-weight: 600;
}

.peer-modal-desc {
  margin: 4px 0 0;
  font-size: 12px;
  line-height: 1.5;
  color: var(--text-secondary);
}

.peer-modal-body {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.peer-modal-grid {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.peer-modal-grid-two {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 12px;
}

.peer-modal-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.peer-modal-inline-input {
  display: flex;
  align-items: center;
  gap: 8px;
}

.peer-modal-notice {
  margin: 0;
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px solid var(--border-card);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 12px;
}

.peer-verify-box {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px;
  border-radius: 10px;
  border: 1px solid var(--border-card-active, var(--border-default));
  background: var(--surface-section);
}

.peer-verify-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  font-size: 12.5px;
}

.peer-verify-juxtaposed {
  display: grid;
  grid-template-columns: minmax(150px, 0.85fr) minmax(0, 1.35fr);
  gap: 12px;
  align-items: stretch;
}

@media (max-width: 520px) {
  .peer-verify-juxtaposed {
    grid-template-columns: 1fr;
  }
}

.peer-verify-identity,
.peer-verify-fingerprint {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 10px;
  border-radius: 8px;
  border: 1px solid var(--border-card);
  background: var(--surface-raised);
  min-width: 0;
}

.peer-verify-fp-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
}

.peer-verify-caption {
  font-size: 11px;
  color: var(--text-muted);
}

.peer-verify-device-name {
  font-size: 14px;
  font-weight: 600;
  color: var(--text-primary);
  overflow-wrap: anywhere;
  margin-bottom: 4px;
}

.peer-verify-device-id {
  font-family: var(--font-mono);
  font-size: 11px;
  color: var(--text-secondary);
  overflow-wrap: anywhere;
}

.peer-fingerprint-blocks {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 4px;
  margin-top: 2px;
}

.peer-fp-chunk {
  padding: 3px 5px;
  border-radius: 4px;
  background: var(--surface-section);
  border: 1px solid var(--border-card);
  font-family: var(--font-mono);
  font-size: 11px;
  font-weight: 600;
  text-align: center;
  color: var(--text-primary);
  letter-spacing: 0.04em;
}

.peer-verify-instruction {
  margin: 0;
  font-size: 11.5px;
  line-height: 1.45;
  color: var(--text-secondary);
}

.peer-verify-confirm-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 8px;
  background: var(--surface-raised);
  border: 1px solid var(--border-card);
  font-size: 12.5px;
  font-weight: 500;
  color: var(--text-primary);
  cursor: pointer;
  user-select: none;
}

.peer-modal-switches {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border-radius: 8px;
  border: 1px solid var(--border-card);
  background: var(--surface-section);
}

.peer-modal-switch-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  font-size: 12.5px;
  color: var(--text-secondary);
}

.peer-modal-error {
  margin: 0;
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px solid color-mix(in srgb, var(--accent-danger) 40%, transparent);
  background: color-mix(in srgb, var(--accent-danger) 8%, var(--surface-section));
  color: var(--accent-danger);
  font-size: 12px;
}

.peer-modal-footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
}

@media (prefers-reduced-motion: reduce) {
  .peer-modal-overlay,
  .peer-modal-surface {
    transition: none;
  }
}
</style>
