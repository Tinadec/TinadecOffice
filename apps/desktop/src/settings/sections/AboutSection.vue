<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { BookOpen, Copy, ExternalLink, FileText, FolderGit2, RefreshCw, Tag } from '@lucide/vue'
import { UiButton, UiSwitch } from '@/components/ui'
import { useDebugStudio } from '@/composables/useDebugStudio'
import { api as baseApi } from '@/api'
import { scopedApi } from '@/lib/storageScope'
import { version as desktopVersion } from '../../../package.json'
import AboutHero from './AboutHero.vue'
const api = scopedApi(baseApi, () => 'user')

/**
 * About: who this is, what is running, what it runs on, where it lives and under which licences.
 * Everything shown is read, not written in: the Desktop version from its package, Core's from its
 * health answer, the runtime from the renderer. Nothing here claims a version it cannot see.
 */
const { t } = useI18n()
const development = import.meta.env.DEV
const repository = 'https://github.com/Tinadec/TinadecOffice'

const { enabled: debugStudioEnabled, loaded: debugStudioLoaded, error: debugStudioLoadError, load: loadDebugStudio, saveEnabled } = useDebugStudio()
const debugStudioSaving = ref(false)
const debugStudioSaveError = ref('')
async function changeDebugStudio(value: boolean): Promise<void> {
  debugStudioSaving.value = true
  debugStudioSaveError.value = ''
  try { await saveEnabled(value) }
  catch (error) { debugStudioSaveError.value = error instanceof Error ? error.message : String(error) }
  finally { debugStudioSaving.value = false }
}

type ServiceState = 'checking' | 'running' | 'unreachable'
const gatewayState = ref<ServiceState>('checking')
const coreState = ref<ServiceState>('checking')
const coreVersion = ref('')

/** Gateway answers for both; when only Core is down it still says that it is up itself. */
async function checkHealth(): Promise<void> {
  gatewayState.value = 'checking'
  coreState.value = 'checking'
  try {
    const probe = await api.healthProbe()
    gatewayState.value = probe.gateway ? 'running' : 'unreachable'
    coreState.value = probe.core ? 'running' : 'unreachable'
    coreVersion.value = probe.core_version ?? ''
  } catch {
    gatewayState.value = 'unreachable'
    coreState.value = 'unreachable'
  }
}

const components = computed(() => [
  { id: 'desktop', name: 'Desktop', role: t('aboutPage.desktopRole'), version: desktopVersion, state: 'running' as ServiceState },
  { id: 'gateway', name: 'Gateway', role: t('aboutPage.gatewayRole'), version: '', state: gatewayState.value },
  { id: 'core', name: 'Core', role: t('aboutPage.coreRole'), version: coreVersion.value, state: coreState.value },
])
const stateLabel = (state: ServiceState) => t(state === 'checking' ? 'aboutPage.checking' : state === 'running' ? 'aboutPage.running' : 'aboutPage.unreachable')

/** The renderer's own description of where it runs; Electron names itself in the user agent. */
const runtime = (() => {
  const agent = typeof navigator === 'undefined' ? '' : navigator.userAgent
  const platform = typeof navigator === 'undefined' ? ''
    : (navigator as Navigator & { userAgentData?: { platform?: string } }).userAgentData?.platform || navigator.platform || ''
  return { electron: /Electron\/([\d.]+)/.exec(agent)?.[1] ?? '', chromium: /Chrome\/([\d.]+)/.exec(agent)?.[1] ?? '', platform }
})()

const licenses = computed(() => [
  { id: 'office', name: t('aboutPage.licenseOffice'), license: 'GPL-3.0-or-later' },
  { id: 'core', name: 'TinadecCore', license: 'MIT' },
  { id: 'gateway', name: 'TinadecGateway', license: 'AGPL-3.0-or-later' },
  { id: 'tools', name: 'TinadecTools', license: 'AGPL-3.0-or-later' },
])

const copyState = ref<'idle' | 'copied' | 'failed'>('idle')
/** A plain-text summary for a bug report: versions, what is reachable, and the runtime. */
async function copyDiagnostics(): Promise<void> {
  const lines = [
    `TinadecOffice Desktop ${desktopVersion}${development ? ' (development)' : ''}`,
    `Gateway: ${gatewayState.value}`,
    `Core${coreVersion.value ? ` ${coreVersion.value}` : ''}: ${coreState.value}`,
    runtime.electron ? `Electron ${runtime.electron} · Chromium ${runtime.chromium}` : `Browser · Chromium ${runtime.chromium || '?'}`,
    `Platform: ${runtime.platform || '?'}`,
  ]
  try {
    await navigator.clipboard.writeText(lines.join('\n'))
    copyState.value = 'copied'
  } catch {
    copyState.value = 'failed'
  }
}

function openExternal(url: string): void {
  window.open(url, '_blank')
}

onMounted(checkHealth)
onMounted(loadDebugStudio)
</script>

<template>
  <div class="about">
    <h2>{{ t('settings.about') }}</h2>
    <AboutHero :version="desktopVersion" :development="development" />

    <section class="general-settings-group" aria-labelledby="about-components-title">
      <div class="general-settings-group-heading">
        <div>
          <h3 id="about-components-title">{{ t('aboutPage.components') }}</h3>
          <p>{{ t('aboutPage.componentsHint') }}</p>
        </div>
        <UiButton variant="ghost" size="sm" :disabled="gatewayState === 'checking'" @click="checkHealth">
          <RefreshCw :size="13" :class="{ 'about-spin': gatewayState === 'checking' }" />{{ t('aboutPage.refresh') }}
        </UiButton>
      </div>
      <ul class="about-list" data-testid="about-health">
        <li v-for="component in components" :key="component.id" class="about-item" :data-state="component.state">
          <span class="about-item-name"><strong>{{ component.name }}</strong><small>{{ component.role }}</small></span>
          <span v-if="component.version" class="about-item-version">{{ component.version }}</span>
          <span class="about-item-state"><i aria-hidden="true" />{{ stateLabel(component.state) }}</span>
        </li>
      </ul>
    </section>

    <section class="general-settings-group" aria-labelledby="about-runtime-title">
      <div class="general-settings-group-heading">
        <div>
          <h3 id="about-runtime-title">{{ t('aboutPage.environment') }}</h3>
          <p>{{ t('aboutPage.environmentHint') }}</p>
        </div>
        <UiButton variant="ghost" size="sm" @click="copyDiagnostics"><Copy :size="13" />{{ t('aboutPage.copyDiagnostics') }}</UiButton>
      </div>
      <dl class="about-facts">
        <div><dt>{{ t('aboutPage.platform') }}</dt><dd>{{ runtime.platform || '—' }}</dd></div>
        <div><dt>Electron</dt><dd>{{ runtime.electron || t('aboutPage.notElectron') }}</dd></div>
        <div><dt>Chromium</dt><dd>{{ runtime.chromium || '—' }}</dd></div>
      </dl>
      <p v-if="copyState !== 'idle'" class="about-note" role="status">{{ t(copyState === 'copied' ? 'aboutPage.copied' : 'aboutPage.copyFailed') }}</p>
    </section>

    <section class="general-settings-group" aria-labelledby="about-links-title">
      <div class="general-settings-group-heading">
        <div><h3 id="about-links-title">{{ t('aboutPage.links') }}</h3></div>
      </div>
      <div class="about-links">
        <button type="button" class="about-link" @click="openExternal(repository)"><FolderGit2 :size="15" /><span>{{ t('aboutPage.repository') }}</span><ExternalLink :size="12" /></button>
        <button type="button" class="about-link" @click="openExternal(`${repository}/releases`)"><Tag :size="15" /><span>{{ t('aboutPage.releases') }}</span><ExternalLink :size="12" /></button>
        <button type="button" class="about-link" @click="openExternal(api.gatewayUrl + '/docs')"><FileText :size="15" /><span>{{ t('settings.apiDocs') }}</span><ExternalLink :size="12" /></button>
      </div>
    </section>

    <section class="general-settings-group" aria-labelledby="about-licenses-title">
      <div class="general-settings-group-heading">
        <div>
          <h3 id="about-licenses-title">{{ t('aboutPage.licenses') }}</h3>
          <p>{{ t('aboutPage.licensesHint') }}</p>
        </div>
        <BookOpen :size="15" class="about-heading-icon" aria-hidden="true" />
      </div>
      <ul class="about-list">
        <li v-for="item in licenses" :key="item.id" class="about-item">
          <span class="about-item-name"><strong>{{ item.name }}</strong></span>
          <span class="about-item-version">{{ item.license }}</span>
        </li>
      </ul>
      <p class="about-note">&copy; {{ new Date().getFullYear() }} Lincube</p>
    </section>

    <section class="general-settings-group" aria-labelledby="about-developer-title">
      <div class="general-settings-group-heading">
        <div><h3 id="about-developer-title">{{ t('aboutPage.developerTools') }}</h3></div>
      </div>
      <div class="about-developer-row">
        <div>
          <label for="about-debug-studio">{{ t('aboutPage.showDebugStudio') }}</label>
          <p id="about-debug-studio-hint">{{ t('aboutPage.debugStudioHint') }}</p>
        </div>
        <UiSwitch id="about-debug-studio" :model-value="debugStudioEnabled" :disabled="!debugStudioLoaded || Boolean(debugStudioLoadError) || debugStudioSaving" aria-describedby="about-debug-studio-hint" @update:model-value="changeDebugStudio" />
      </div>
      <p v-if="debugStudioLoadError || debugStudioSaveError" class="about-developer-error" role="alert">
        {{ t(debugStudioLoadError ? 'aboutPage.debugStudioLoadFailed' : 'aboutPage.debugStudioSaveFailed') }}: {{ debugStudioSaveError || String(debugStudioLoadError) }}
      </p>
    </section>
  </div>
</template>

<style scoped>
.about-list { display: grid; margin: 14px 0 0; padding: 0; list-style: none; }
.about-item { display: flex; align-items: center; gap: 12px; padding: 10px 0; }
.about-item + .about-item { border-top: 1px solid color-mix(in srgb, var(--text-primary) 7%, transparent); }
.about-item-name { display: grid; gap: 2px; flex: 1; min-width: 0; }
.about-item-name strong { font-size: 13px; font-weight: 600; }
.about-item-name small { font-size: 12px; color: var(--text-muted); }
.about-item-version { font-size: 12px; color: var(--text-secondary); font-variant-numeric: tabular-nums; }
.about-item-state { display: inline-flex; align-items: center; gap: 6px; min-width: 64px; justify-content: flex-end; font-size: 12px; color: var(--text-muted); }
.about-item-state i { width: 7px; height: 7px; border-radius: 50%; background: var(--text-muted); }
.about-item[data-state="running"] .about-item-state { color: var(--accent-success); }
.about-item[data-state="running"] .about-item-state i { background: var(--accent-success); box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent-success) 18%, transparent); }
.about-item[data-state="unreachable"] .about-item-state { color: var(--accent-danger); }
.about-item[data-state="unreachable"] .about-item-state i { background: var(--accent-danger); }
.about-facts { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; margin: 14px 0 0; }
.about-facts div { min-width: 0; }
.about-facts dt { font-size: 12px; color: var(--text-muted); }
.about-facts dd { margin: 2px 0 0; font-size: 13px; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
.about-links { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 12px; }
.about-link { display: inline-flex; align-items: center; gap: 8px; padding: 8px 12px; border: 0; border-radius: 10px; background: var(--surface-input); color: var(--text-primary); font: inherit; font-size: 13px; cursor: pointer; }
.about-link:hover { background: var(--surface-hover); }
.about-link:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 2px; }
.about-link svg:last-child { color: var(--text-muted); }
.about-heading-icon { color: var(--text-muted); flex-shrink: 0; }
.about-note { margin: 10px 0 0; font-size: 12px; color: var(--text-muted); }
.about-developer-row { display: flex; align-items: center; gap: 16px; justify-content: space-between; margin-top: 12px; }
.about-developer-row > div { min-width: 0; }
.about-developer-row label { font-size: 13px; font-weight: 500; }
.about-developer-row p { margin: 4px 0 0; font-size: 12px; color: var(--text-muted); }
.about-developer-error { margin: 10px 0 0; font-size: 12px; color: var(--text-error); overflow-wrap: anywhere; }
.about-spin { animation: about-spin 0.9s linear infinite; }
@keyframes about-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .about-spin { animation: none; } }
@media (max-width: 560px) { .about-facts { grid-template-columns: 1fr; } }
</style>
