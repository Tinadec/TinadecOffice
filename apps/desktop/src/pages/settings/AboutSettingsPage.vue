<script setup lang="ts">
import { ChevronRight, Cpu, FileText, Globe, Monitor } from '@lucide/vue'
import { UiButton, UiCard } from '@/components/ui'
import BrandLogo from '@/components/BrandLogo.vue'
import { useAboutSettings } from './aboutSettings'

const { aboutGatewayStatus, openExternal, t } = useAboutSettings()
</script>

<template>
  <h1>{{ t('settings.about') }}</h1>

            <!-- App identity -->
            <div class="about-brand">
              <div class="about-brand-icon">
                <BrandLogo :size="28" />
              </div>
              <div class="about-brand-text">
                <span class="about-brand-name">TinadecOffice</span>
                <span class="about-brand-ver">v0.1.0</span>
              </div>
            </div>

            <!-- Runtime status -->
            <div class="about-status-grid">
              <div class="about-status-card">
                <div class="about-status-row">
                  <span class="about-status-label">Gateway</span>
                  <span class="about-status-dot" :class="aboutGatewayStatus === 'ok' ? 'ok' : 'off'" />
                  <span class="about-status-text" :class="aboutGatewayStatus === 'ok' ? 'ok' : 'off'">
                    {{ aboutGatewayStatus === 'ok' ? t('aboutPage.running') : t('aboutPage.unreachable') }}
                  </span>
                </div>
              </div>
            </div>

            <!-- Component versions -->
            <UiCard class="about-section">
              <div class="about-row">
                <span>{{ t('settings.versionDesktop') }}</span>
                <span>0.1.0</span>
              </div>
              <div class="about-row">
                <span>{{ t('settings.versionCode') }}</span>
                <span>0.1.0</span>
              </div>
              <div class="about-row">
                <span>{{ t('settings.versionCore') }}</span>
                <span>0.1.0</span>
              </div>
            </UiCard>

            <!-- Architecture -->
            <div class="about-arch">
              <h2>{{ t('aboutPage.architecture') }}</h2>
              <p class="about-decouple-hint">{{ t('settings.decoupleHint') }}</p>
              <div class="about-layers">
                <div class="about-layer">
                  <div class="about-layer-header">
                    <Monitor :size="14" />
                    <span>Desktop</span>
                  </div>
                  <div class="about-layer-tech">Electron + <span class="about-tech-token">Vue 3</span></div>
                  <div class="about-layer-port">:5173</div>
                </div>
                <div class="about-layer-arrow">
                  <ChevronRight :size="14" />
                </div>
                <div class="about-layer">
                  <div class="about-layer-header">
                    <Globe :size="14" />
                    <span>Gateway</span>
                  </div>
                  <div class="about-layer-tech">Elysia + Node.js</div>
                  <div class="about-layer-port">:48730</div>
                </div>
                <div class="about-layer-arrow">
                  <ChevronRight :size="14" />
                </div>
                <div class="about-layer about-layer--core">
                  <div class="about-layer-header">
                    <Cpu :size="14" />
                    <span>Core</span>
                  </div>
                  <div class="about-layer-tech">.NET 10 + SQLite</div>
                  <div class="about-layer-port">:48731</div>
                </div>
              </div>
            </div>

            <!-- Links -->
            <div class="about-links">
              <UiButton variant="outline" size="sm" class="about-link-btn" @click="openExternal('https://github.com/apanzinc/TinadecCode')">
                <Globe :size="14" />
                <span>GitHub</span>
              </UiButton>
              <UiButton variant="outline" size="sm" class="about-link-btn" @click="openExternal('http://127.0.0.1:48730/docs')">
                <FileText :size="14" />
                <span>{{ t('settings.apiDocs') }}</span>
              </UiButton>
            </div>

  <p class="about-license">&copy; {{ new Date().getFullYear() }} TinadecOffice &middot; GPL-3.0-or-later</p>
</template>
