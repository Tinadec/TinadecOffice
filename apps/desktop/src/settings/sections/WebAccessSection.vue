<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Globe, KeyRound, Lock, LogOut, RefreshCw, Save, ShieldCheck } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiLabel, UiSwitch, UiTextarea } from '@/components/ui'
import { useHostAccess } from '@/lib/hostAccess'
import { resolveWebGatewayBase } from '@/lib/hostConnection'
import { useNotifications } from '@/composables/useNotifications'

interface WebAuthSettingsDto {
  allow_loopback_auto_login: boolean
  password_configured: boolean
  oidc_enabled: boolean
  allowed_subjects: string[]
  allowed_emails: string[]
  cloudflare_access_enabled: boolean
  cloudflare_access_team_domain: string
  cloudflare_access_aud: string
  session_ttl_seconds: number
  trust_proxy: 'false' | 'true' | 'cloudflare'
  updated_at: string
}

const { t } = useI18n()
const { status: hostStatus } = useHostAccess()
const { notify } = useNotifications()

const loading = ref(false)
const saving = ref(false)
const loadError = ref<string | null>(null)
const dirty = ref(false)

const allowLoopbackAutoLogin = ref(true)
const passwordConfigured = ref(false)
const newPassword = ref('')
const confirmPassword = ref('')
const oidcEnabled = ref(false)
const allowedSubjectsText = ref('')
const allowedEmailsText = ref('')
const cloudflareAccessEnabled = ref(false)
const cloudflareTeamDomain = ref('')
const cloudflareAud = ref('')
const trustProxy = ref<'false' | 'true' | 'cloudflare'>('false')

const currentTier = computed(() => hostStatus.value?.webAuth?.tier ?? 'electron')
const currentAuthMethod = computed(() => hostStatus.value?.webAuth?.principal?.auth_method ?? 'electron_host')

function parseList(raw: string): string[] {
  return raw
    .split(/[\n,]/)
    .map((item) => item.trim())
    .filter(Boolean)
}

async function loadSettings() {
  loading.value = true
  loadError.value = null
  try {
    const origin = resolveWebGatewayBase()
    const response = await fetch(`${origin}/api/v1/web-auth/settings`, {
      credentials: 'include',
      headers: { accept: 'application/json' },
    })
    if (!response.ok) {
      loadError.value = t('webAccess.loadFailed')
      return
    }
    const data = (await response.json()) as WebAuthSettingsDto
    allowLoopbackAutoLogin.value = data.allow_loopback_auto_login
    passwordConfigured.value = data.password_configured
    oidcEnabled.value = data.oidc_enabled
    allowedSubjectsText.value = data.allowed_subjects.join('\n')
    allowedEmailsText.value = data.allowed_emails.join('\n')
    cloudflareAccessEnabled.value = data.cloudflare_access_enabled
    cloudflareTeamDomain.value = data.cloudflare_access_team_domain || ''
    cloudflareAud.value = data.cloudflare_access_aud || ''
    trustProxy.value = data.trust_proxy || 'false'
    dirty.value = false
  } catch {
    loadError.value = t('webAccess.loadFailed')
  } finally {
    loading.value = false
  }
}

async function saveSettings(revokeExisting = false) {
  if (saving.value) return
  if (newPassword.value && newPassword.value !== confirmPassword.value) {
    notify.error(new Error(t('webAccess.passwordMismatch')))
    return
  }
  saving.value = true
  try {
    const origin = resolveWebGatewayBase()
    const body: Record<string, unknown> = {
      allow_loopback_auto_login: allowLoopbackAutoLogin.value,
      oidc_enabled: oidcEnabled.value,
      allowed_subjects: parseList(allowedSubjectsText.value),
      allowed_emails: parseList(allowedEmailsText.value),
      cloudflare_access_enabled: cloudflareAccessEnabled.value,
      cloudflare_access_team_domain: cloudflareTeamDomain.value.trim(),
      cloudflare_access_aud: cloudflareAud.value.trim(),
      revoke_existing_sessions: revokeExisting,
    }
    if (newPassword.value) {
      body.new_password = newPassword.value
    }
    const response = await fetch(`${origin}/api/v1/web-auth/settings`, {
      method: 'PUT',
      credentials: 'include',
      headers: {
        'content-type': 'application/json',
        accept: 'application/json',
      },
      body: JSON.stringify(body),
    })
    const payload = (await response.json().catch(() => ({}))) as WebAuthSettingsDto & { detail?: string; message?: string }
    if (!response.ok) {
      throw new Error(payload.detail || payload.message || t('webAccess.saveFailed'))
    }
    passwordConfigured.value = payload.password_configured
    newPassword.value = ''
    confirmPassword.value = ''
    dirty.value = false
    notify.success(revokeExisting ? t('webAccess.sessionsRevoked') : t('webAccess.saved'))
  } catch (err) {
    notify.error(err, { title: t('webAccess.saveFailed') })
  } finally {
    saving.value = false
  }
}

async function clearOwnerPassword() {
  if (saving.value) return
  saving.value = true
  try {
    const origin = resolveWebGatewayBase()
    const response = await fetch(`${origin}/api/v1/web-auth/settings`, {
      method: 'PUT',
      credentials: 'include',
      headers: { 'content-type': 'application/json', accept: 'application/json' },
      body: JSON.stringify({ clear_password: true }),
    })
    if (!response.ok) throw new Error(t('webAccess.saveFailed'))
    passwordConfigured.value = false
    notify.success(t('webAccess.saved'))
  } catch (err) {
    notify.error(err, { title: t('webAccess.saveFailed') })
  } finally {
    saving.value = false
  }
}

onMounted(() => {
  void loadSettings()
})

defineExpose({
  canLeave: async () => true,
})
</script>

<template>
  <section class="settings-section web-access-section">
    <header class="web-access-header">
      <div class="web-access-title">
        <Globe :size="18" />
        <div>
          <h2>{{ t('webAccess.title') }}</h2>
          <p>{{ t('webAccess.description') }}</p>
        </div>
      </div>
      <div class="web-access-badges">
        <UiBadge variant="outline">{{ t('webAccess.tierLabel', { tier: currentTier }) }}</UiBadge>
        <UiBadge variant="secondary">{{ currentAuthMethod }}</UiBadge>
      </div>
    </header>

    <div v-if="loadError" class="web-access-alert">
      <span>{{ loadError }}</span>
      <UiButton variant="outline" size="sm" :disabled="loading" @click="loadSettings">
        <RefreshCw :size="14" />
        <span>{{ t('common.retry') }}</span>
      </UiButton>
    </div>

    <div class="web-access-card">
      <div class="web-access-row">
        <div>
          <strong>{{ t('webAccess.loopbackAutoLogin') }}</strong>
          <p>{{ t('webAccess.loopbackAutoLoginHint') }}</p>
        </div>
        <UiSwitch v-model="allowLoopbackAutoLogin" @update:model-value="dirty = true" />
      </div>
    </div>

    <div class="web-access-card">
      <div class="web-access-card-head">
        <Lock :size="16" />
        <h3>{{ t('webAccess.passwordSection') }}</h3>
        <UiBadge :variant="passwordConfigured ? 'default' : 'outline'">
          {{ passwordConfigured ? t('webAccess.passwordSet') : t('webAccess.passwordUnset') }}
        </UiBadge>
      </div>
      <div class="web-access-grid">
        <div class="web-access-field">
          <UiLabel for="web-access-new-password">{{ t('webAccess.newPassword') }}</UiLabel>
          <UiInput
            id="web-access-new-password"
            v-model="newPassword"
            type="password"
            autocomplete="new-password"
            :placeholder="t('webAccess.newPasswordPlaceholder')"
            @update:model-value="dirty = true"
          />
        </div>
        <div class="web-access-field">
          <UiLabel for="web-access-confirm-password">{{ t('webAccess.confirmPassword') }}</UiLabel>
          <UiInput
            id="web-access-confirm-password"
            v-model="confirmPassword"
            type="password"
            autocomplete="new-password"
            :placeholder="t('webAccess.confirmPasswordPlaceholder')"
            @update:model-value="dirty = true"
          />
        </div>
      </div>
      <div v-if="passwordConfigured" class="web-access-inline-actions">
        <UiButton variant="ghost" size="sm" :disabled="saving" @click="clearOwnerPassword">
          {{ t('webAccess.clearPassword') }}
        </UiButton>
      </div>
    </div>

    <div class="web-access-card">
      <div class="web-access-row">
        <div class="web-access-card-head-inline">
          <KeyRound :size="16" />
          <div>
            <strong>{{ t('webAccess.oidcSection') }}</strong>
            <p>{{ t('webAccess.oidcHint') }}</p>
          </div>
        </div>
        <UiSwitch v-model="oidcEnabled" @update:model-value="dirty = true" />
      </div>
      <div class="web-access-grid">
        <div class="web-access-field">
          <UiLabel for="web-access-subjects">{{ t('webAccess.allowedSubjects') }}</UiLabel>
          <UiTextarea
            id="web-access-subjects"
            v-model="allowedSubjectsText"
            :rows="3"
            :placeholder="t('webAccess.allowedSubjectsPlaceholder')"
            @update:model-value="dirty = true"
          />
        </div>
        <div class="web-access-field">
          <UiLabel for="web-access-emails">{{ t('webAccess.allowedEmails') }}</UiLabel>
          <UiTextarea
            id="web-access-emails"
            v-model="allowedEmailsText"
            :rows="3"
            :placeholder="t('webAccess.allowedEmailsPlaceholder')"
            @update:model-value="dirty = true"
          />
        </div>
      </div>
    </div>

    <div class="web-access-card">
      <div class="web-access-row">
        <div class="web-access-card-head-inline">
          <ShieldCheck :size="16" />
          <div>
            <strong>{{ t('webAccess.cloudflareSection') }}</strong>
            <p>{{ t('webAccess.cloudflareHint', { trustProxy }) }}</p>
          </div>
        </div>
        <UiSwitch v-model="cloudflareAccessEnabled" @update:model-value="dirty = true" />
      </div>
      <div class="web-access-grid">
        <div class="web-access-field">
          <UiLabel for="web-access-cf-domain">{{ t('webAccess.cfTeamDomain') }}</UiLabel>
          <UiInput
            id="web-access-cf-domain"
            v-model="cloudflareTeamDomain"
            placeholder="myteam.cloudflareaccess.com"
            @update:model-value="dirty = true"
          />
        </div>
        <div class="web-access-field">
          <UiLabel for="web-access-cf-aud">{{ t('webAccess.cfAudience') }}</UiLabel>
          <UiInput
            id="web-access-cf-aud"
            v-model="cloudflareAud"
            placeholder="Cloudflare Access Application AUD"
            @update:model-value="dirty = true"
          />
        </div>
      </div>
      <p class="web-access-cf-limit-note">{{ t('webAccess.cloudflareLimitNote') }}</p>
    </div>

    <footer class="web-access-footer">
      <UiButton variant="outline" size="sm" :disabled="saving" @click="saveSettings(true)">
        <LogOut :size="14" />
        <span>{{ t('webAccess.revokeSessions') }}</span>
      </UiButton>
      <UiButton size="sm" :disabled="saving" @click="saveSettings(false)">
        <Save :size="14" />
        <span>{{ t('webAccess.save') }}</span>
      </UiButton>
    </footer>
  </section>
</template>

<style scoped>
.web-access-section {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.web-access-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.web-access-title {
  display: flex;
  align-items: flex-start;
  gap: 10px;
}

.web-access-title h2 {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
}

.web-access-title p {
  margin: 4px 0 0;
  font-size: 12.5px;
  color: var(--text-secondary);
}

.web-access-badges {
  display: flex;
  gap: 6px;
}

.web-access-alert {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 14px;
  border-radius: 8px;
  border: 1px solid var(--border-default);
  background: var(--surface-section);
  font-size: 12.5px;
}

.web-access-card {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 16px;
  border-radius: 10px;
  border: 1px solid var(--border-default);
  background: var(--surface-section);
}

.web-access-card-head {
  display: flex;
  align-items: center;
  gap: 8px;
}

.web-access-card-head h3 {
  margin: 0;
  font-size: 14px;
  font-weight: 600;
  flex: 1;
}

.web-access-card-head-inline {
  display: flex;
  align-items: flex-start;
  gap: 8px;
}

.web-access-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}

.web-access-row strong {
  font-size: 13.5px;
}

.web-access-row p {
  margin: 3px 0 0;
  font-size: 12px;
  color: var(--text-secondary);
}

.web-access-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 12px;
}

.web-access-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.web-access-inline-actions {
  display: flex;
  justify-content: flex-end;
}

.web-access-cf-limit-note {
  margin: 0;
  font-size: 12px;
  color: var(--text-muted);
}

.web-access-footer {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
}
</style>
