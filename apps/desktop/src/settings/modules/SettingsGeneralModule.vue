<script lang="ts">
import { defineSettingsModule } from '../defineSettingsModule'

export default defineSettingsModule('SettingsGeneralModule')
</script>

<template>
<div class="settings-module" data-settings-module="general">
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
        </div>
</template>
