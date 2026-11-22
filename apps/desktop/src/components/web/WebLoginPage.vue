<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { KeyRound, Loader2, Lock, RefreshCw, ShieldCheck } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiLabel } from '@/components/ui'
import BrandLogo from '@/components/BrandLogo.vue'
import { useHostAccess } from '@/lib/hostAccess'
import { resolveWebGatewayBase, type WebAuthStatusSnapshot } from '@/lib/hostConnection'
import { retryConnection } from '@/composables/useConnection'

const emit = defineEmits<{
  authenticated: []
}>()

const { t } = useI18n()
const { status } = useHostAccess()

const password = ref('')
const submitting = ref(false)
const refreshing = ref(false)
const errorMessage = ref<string | null>(null)
const liveStatus = ref<WebAuthStatusSnapshot | null>(null)

const authInfo = computed(() => liveStatus.value ?? status.value?.webAuth ?? null)
const passwordEnabled = computed(() => Boolean(authInfo.value?.password_configured))
const oidcEnabled = computed(() => Boolean(authInfo.value?.oidc_enabled))
const cloudflareEnabled = computed(() => Boolean(authInfo.value?.cloudflare_access_enabled))
const hasAnyProvider = computed(() => passwordEnabled.value || oidcEnabled.value || cloudflareEnabled.value)

async function refreshStatus() {
  refreshing.value = true
  try {
    const origin = resolveWebGatewayBase()
    const res = await fetch(`${origin}/api/v1/web-auth/status`, {
      credentials: 'include',
      headers: { accept: 'application/json' },
    })
    if (res.ok) {
      const data = (await res.json()) as WebAuthStatusSnapshot
      liveStatus.value = data
      if (data.authenticated) {
        await retryConnection()
        emit('authenticated')
      }
    }
  } catch {
    // Keep current host status when probe fails.
  } finally {
    refreshing.value = false
  }
}

async function submitPasswordLogin() {
  if (submitting.value || !password.value) return
  submitting.value = true
  errorMessage.value = null
  try {
    const origin = resolveWebGatewayBase()
    const response = await fetch(`${origin}/api/v1/web-auth/login`, {
      method: 'POST',
      credentials: 'include',
      headers: {
        'content-type': 'application/json',
        accept: 'application/json',
      },
      body: JSON.stringify({
        provider: 'local_password',
        password: password.value,
      }),
    })
    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { detail?: string; message?: string; code?: string }
      if (response.status === 429) {
        errorMessage.value = t('webAuth.rateLimited')
      } else {
        errorMessage.value = data.detail || data.message || t('webAuth.invalidCredentials')
      }
      return
    }
    password.value = ''
    await retryConnection()
    emit('authenticated')
  } catch {
    errorMessage.value = t('webAuth.networkError')
  } finally {
    submitting.value = false
  }
}

function startOidcLogin() {
  const origin = resolveWebGatewayBase()
  const returnTo = `${window.location.pathname}${window.location.hash || '#/'}`
  window.location.href = `${origin}/api/v1/web-auth/oidc/login?return_to=${encodeURIComponent(returnTo)}`
}

onMounted(() => {
  if (!status.value?.webAuth) {
    void refreshStatus()
  }
})
</script>

<template>
  <div class="web-login-backdrop" role="dialog" aria-modal="true" :aria-label="t('webAuth.title')">
    <div class="web-login-card">
      <div class="web-login-brand">
        <BrandLogo class="web-login-logo" />
        <div class="web-login-heading">
          <h1>{{ t('webAuth.title') }}</h1>
          <p>{{ t('webAuth.subtitle') }}</p>
        </div>
        <UiBadge variant="outline">{{ authInfo?.tier === 'loopback' ? t('webAuth.tierLoopback') : t('webAuth.tierRemote') }}</UiBadge>
      </div>

      <div v-if="errorMessage" class="web-login-error" role="alert">
        {{ errorMessage }}
      </div>

      <form v-if="passwordEnabled" class="web-login-form" @submit.prevent="submitPasswordLogin">
        <div class="web-login-field">
          <UiLabel for="web-owner-password">{{ t('webAuth.passwordLabel') }}</UiLabel>
          <UiInput
            id="web-owner-password"
            v-model="password"
            type="password"
            autocomplete="current-password"
            :placeholder="t('webAuth.passwordPlaceholder')"
            :disabled="submitting"
          />
        </div>
        <UiButton type="submit" class="web-login-submit" :disabled="submitting || !password">
          <Loader2 v-if="submitting" :size="15" class="spin" />
          <Lock v-else :size="15" />
          <span>{{ submitting ? t('webAuth.signingIn') : t('webAuth.signInPassword') }}</span>
        </UiButton>
      </form>

      <div v-if="oidcEnabled" class="web-login-provider">
        <UiButton variant="outline" class="web-login-submit" @click="startOidcLogin">
          <KeyRound :size="15" />
          <span>{{ t('webAuth.signInOidc') }}</span>
        </UiButton>
      </div>

      <div v-if="cloudflareEnabled" class="web-login-provider">
        <div class="web-login-cf-note">
          <ShieldCheck :size="15" />
          <span>{{ t('webAuth.cloudflareAccessHint') }}</span>
        </div>
        <UiButton variant="outline" class="web-login-submit" :disabled="refreshing" @click="refreshStatus">
          <RefreshCw :size="15" :class="{ spin: refreshing }" />
          <span>{{ t('webAuth.retryAssertion') }}</span>
        </UiButton>
      </div>

      <div v-if="!hasAnyProvider" class="web-login-unconfigured">
        <p>{{ t('webAuth.noProviderConfigured') }}</p>
        <UiButton variant="outline" size="sm" :disabled="refreshing" @click="refreshStatus">
          <RefreshCw :size="14" :class="{ spin: refreshing }" />
          <span>{{ t('common.retry') }}</span>
        </UiButton>
      </div>
    </div>
  </div>
</template>

<style scoped>
.web-login-backdrop {
  position: fixed;
  inset: 0;
  z-index: 200;
  display: grid;
  place-items: center;
  padding: 24px;
  background: var(--bg-primary);
}

.web-login-card {
  width: min(420px, 100%);
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 28px;
  border-radius: var(--radius-lg, 14px);
  border: 1px solid var(--border-default);
  background: var(--surface-raised, var(--bg-secondary));
  box-shadow: var(--shadow-panel, 0 18px 50px rgba(0, 0, 0, 0.25));
}

.web-login-brand {
  display: flex;
  align-items: center;
  gap: 12px;
}

.web-login-logo {
  width: 34px;
  height: 34px;
  flex-shrink: 0;
  color: var(--accent-primary);
}

.web-login-heading {
  flex: 1;
  min-width: 0;
}

.web-login-heading h1 {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
  color: var(--text-primary);
}

.web-login-heading p {
  margin: 2px 0 0;
  font-size: 12.5px;
  color: var(--text-secondary);
}

.web-login-error {
  padding: 10px 12px;
  border-radius: 8px;
  background: var(--bg-error, rgba(220, 38, 38, 0.12));
  color: var(--text-error, #ef4444);
  font-size: 12.5px;
}

.web-login-form {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.web-login-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.web-login-submit {
  width: 100%;
  justify-content: center;
  gap: 8px;
}

.web-login-provider {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.web-login-cf-note {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12.5px;
  color: var(--text-secondary);
}

.web-login-unconfigured {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px;
  border-radius: 8px;
  border: 1px dashed var(--border-default);
  font-size: 12.5px;
  color: var(--text-secondary);
}

.web-login-unconfigured p {
  margin: 0;
}

.spin {
  animation: web-login-spin 0.9s linear infinite;
}

@keyframes web-login-spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
</style>
