<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { FolderOpen, Globe, Monitor, Sparkles, Sun, Moon, Laptop } from '@lucide/vue'
import { UiButton, UiSelectField } from '@/components/ui'
import { useTheme, type Theme } from '@/composables/useTheme'
import { useHostAccess } from '@/lib/hostAccess'
import { canPickHostDirectories, isElectronHost, pickWorkspaceFolders } from '@/lib/hostConnection'
import { homeController } from '@/controllers/HomeController'

const { t, te, locale } = useI18n()
const { theme, setTheme } = useTheme()
const { isPreview } = useHostAccess()

function label(key: string, fallback: string): string {
  return te(key) ? String(t(key)) : fallback
}

const isElectron = computed(() => isElectronHost() && !isPreview.value)
const canPickFolder = computed(() => !isPreview.value && canPickHostDirectories())

const selectedFolder = ref(homeController.currentProject.value?.path ?? '')
const pickingFolder = ref(false)
const folderError = ref('')

const languageOptions = [
  { value: 'zh-CN', label: '简体中文' },
  { value: 'zh-TW', label: '繁體中文' },
  { value: 'en', label: 'English' },
  { value: 'ja', label: '日本語' },
  { value: 'ru', label: 'Русский' },
]

const themeOptions: Array<{ value: Theme; icon: typeof Sun; key: string; fallback: string }> = [
  { value: 'system', icon: Laptop, key: 'settings.themeSystem', fallback: 'System' },
  { value: 'light', icon: Sun, key: 'settings.themeLight', fallback: 'Light' },
  { value: 'dark', icon: Moon, key: 'settings.themeDark', fallback: 'Dark' },
]

function onChangeLocale(nextLocale: string): void {
  if (!nextLocale) return
  locale.value = nextLocale
  try {
    localStorage.setItem('tinadec-locale', nextLocale)
  } catch {
    // Ignore storage quota errors.
  }
  window.tinadec?.setInterfaceLocale?.(nextLocale)
}

async function pickWorkspaceFolder(): Promise<void> {
  if (!canPickFolder.value) return
  pickingFolder.value = true
  folderError.value = ''
  try {
    const paths = await pickWorkspaceFolders(label('oobe.welcome.workspaceLabel', 'Workspace Folder'), { multiple: false })
    const chosen = paths?.[0]
    if (chosen) {
      selectedFolder.value = chosen
      if (typeof homeController.openProject === 'function') {
        // Keep registration lightweight; if project creation helper is available, register via API.
        try {
          const { api } = await import('@/api')
          if (typeof api.createProject === 'function') {
            const name = chosen.split(/[\\/]/).filter(Boolean).pop() || 'Workspace'
            const created = await api.createProject(name, chosen)
            if (created?.id) {
              void homeController.setSelectedProject(created.id)
            }
          }
        } catch {
          // Folder path is still displayed even if already registered.
        }
      }
    }
  } catch (err) {
    folderError.value = err instanceof Error ? err.message : String(err)
  } finally {
    pickingFolder.value = false
  }
}
</script>

<template>
  <div class="oobe-step" data-testid="oobe-step-welcome">
    <div class="oobe-step-hero">
      <div class="oobe-step-badge">
        <Sparkles :size="14" aria-hidden="true" />
        <span>{{ label('oobe.welcome.timeHint', '~2 minutes · Only 1 step required') }}</span>
      </div>
      <h3>{{ label('oobe.welcome.title', 'Welcome to TinadecOffice') }}</h3>
      <p>{{ label('oobe.welcome.subtitle', 'Set your language, appearance, and workspace in under 2 minutes — only connecting a model is required.') }}</p>
    </div>

    <div class="oobe-card-grid">
      <div class="oobe-field-card">
        <label class="oobe-field-label">{{ label('oobe.welcome.languageLabel', 'Interface Language') }}</label>
        <UiSelectField
          :model-value="String(locale)"
          :options="languageOptions"
          :aria-label="label('oobe.welcome.languageLabel', 'Interface Language')"
          @update:model-value="onChangeLocale"
        />
      </div>

      <div class="oobe-field-card">
        <label class="oobe-field-label">{{ label('oobe.welcome.themeLabel', 'Appearance') }}</label>
        <div class="oobe-segmented">
          <button
            v-for="item in themeOptions"
            :key="item.value"
            type="button"
            class="oobe-seg-btn"
            :class="{ active: theme === item.value }"
            @click="setTheme(item.value)"
          >
            <component :is="item.icon" :size="14" aria-hidden="true" />
            <span>{{ label(item.key, item.fallback) }}</span>
          </button>
        </div>
      </div>
    </div>

    <div class="oobe-field-card">
      <div class="oobe-env-head">
        <component :is="isElectron ? Monitor : Globe" :size="15" aria-hidden="true" />
        <label class="oobe-field-label">{{ label('oobe.welcome.workspaceLabel', 'Workspace Folder') }}</label>
      </div>
      <template v-if="canPickFolder">
        <div class="oobe-folder-row">
          <span class="oobe-folder-path">{{ selectedFolder || label('oobe.welcome.workspacePick', 'Choose folder...') }}</span>
          <UiButton variant="outline" size="sm" :disabled="pickingFolder" @click="pickWorkspaceFolder">
            <FolderOpen :size="14" />
            <span>{{ label('oobe.welcome.workspacePick', 'Choose folder...') }}</span>
          </UiButton>
        </div>
        <p v-if="folderError" class="oobe-inline-error">{{ folderError }}</p>
      </template>
      <p v-if="!isElectron" class="oobe-env-note" data-testid="oobe-web-mode-note">
        {{ label('oobe.welcome.webHostNote', 'Connected in Web mode — host access and remote workspace permissions are managed by the Gateway.') }}
      </p>
    </div>
  </div>
</template>

<style scoped>
.oobe-step { display: flex; flex-direction: column; gap: 14px; }
.oobe-step-hero h3 { margin: 8px 0 4px; font-size: 18px; color: var(--text-primary); }
.oobe-step-hero p { margin: 0; font-size: 13px; color: var(--text-secondary); line-height: 1.55; }
.oobe-step-badge {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 10px;
  border-radius: 999px;
  font-size: 11.5px;
  color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 12%, transparent);
}
.oobe-card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 10px;
}
.oobe-field-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px;
  border-radius: 11px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}
.oobe-field-label {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
}
.oobe-segmented {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 6px;
}
.oobe-seg-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 5px;
  padding: 7px 8px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  cursor: pointer;
  font-size: 12px;
}
.oobe-seg-btn.active {
  border-color: var(--accent-primary);
  color: var(--text-primary);
  background: color-mix(in srgb, var(--accent-primary) 12%, var(--surface-section));
}
.oobe-env-head { display: flex; align-items: center; gap: 6px; color: var(--text-primary); }
.oobe-folder-row { display: flex; align-items: center; justify-content: space-between; gap: 10px; flex-wrap: wrap; }
.oobe-folder-path {
  flex: 1;
  min-width: 0;
  font-size: 12px;
  font-family: var(--font-mono);
  color: var(--text-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.oobe-env-note { margin: 0; font-size: 12px; color: var(--text-secondary); line-height: 1.5; }
.oobe-inline-error { margin: 0; font-size: 12px; color: var(--accent-danger); }
</style>
