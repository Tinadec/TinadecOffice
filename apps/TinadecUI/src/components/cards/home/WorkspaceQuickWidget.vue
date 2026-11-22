<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { FileCode, FolderTree, Globe, TerminalSquare } from '@lucide/vue'
import { UiIslandCard } from '@/components/ui'
import type { ProjectDto } from '@/api'
import { editorPath } from '@/lib/documentWorkspace'

const props = defineProps<{
  project: ProjectDto | null
  rootCount?: number
}>()

const emit = defineEmits<{
  'open-code': []
  'open-terminal': []
  'open-browser': []
  'open-workspace': []
}>()

const { t, te } = useI18n()

function label(key: string, fallback: string, params?: Record<string, unknown>): string {
  return te(key) ? String(t(key, params ?? {})) : fallback
}

const activeFileName = computed(() => {
  const path = editorPath.value
  if (!path) return ''
  return path.split(/[\\/]/).filter(Boolean).pop() ?? path
})

const effectiveRootCount = computed(() => {
  if (typeof props.rootCount === 'number' && props.rootCount > 0) return props.rootCount
  return props.project ? 1 : 0
})
</script>

<template>
  <UiIslandCard variant="raised" padding="none" class="bento-widget workspace-quick-widget" data-testid="workspace-quick-widget">
    <div class="bento-widget-inner">
      <div class="bento-widget-head">
        <span class="bento-widget-icon is-ws">
          <FolderTree :size="15" aria-hidden="true" />
        </span>
        <span class="bento-widget-title">{{ label('context.bentoWorkspaceTitle', 'Workspace') }}</span>
        <button
          v-if="effectiveRootCount > 0"
          type="button"
          class="bento-root-badge"
          @click="emit('open-workspace')"
        >
          {{ label('context.bentoWorkspaceRoots', `${effectiveRootCount} roots`, { count: effectiveRootCount }) }}
        </button>
      </div>

      <div class="bento-ws-meta">
        <strong v-if="project" class="bento-ws-name" :title="project.path">{{ project.name }}</strong>
        <span v-else class="bento-ws-empty">{{ label('context.bentoWorkspaceNoProject', 'No project selected') }}</span>
        <code v-if="activeFileName" class="bento-ws-file" :title="editorPath ?? ''">{{ activeFileName }}</code>
      </div>

      <div class="bento-widget-links">
        <button type="button" class="bento-chip-btn" data-testid="workspace-open-code" @click="emit('open-code')">
          <FileCode :size="12" aria-hidden="true" />
          <span>{{ label('context.bentoOpenCode', 'Editor') }}</span>
        </button>
        <button type="button" class="bento-chip-btn" data-testid="workspace-open-terminal" @click="emit('open-terminal')">
          <TerminalSquare :size="12" aria-hidden="true" />
          <span>{{ label('context.bentoOpenTerminal', 'Terminal') }}</span>
        </button>
        <button type="button" class="bento-chip-btn" data-testid="workspace-open-browser" @click="emit('open-browser')">
          <Globe :size="12" aria-hidden="true" />
          <span>{{ label('context.bentoOpenBrowser', 'Preview') }}</span>
        </button>
      </div>
    </div>
  </UiIslandCard>
</template>

<style scoped>
.bento-widget { width: 100%; min-width: 0; flex: none; }
.bento-widget-inner { display: flex; flex-direction: column; gap: 8px; padding: 12px 13px; min-width: 0; }
.bento-widget-head { display: flex; align-items: center; gap: 8px; min-width: 0; }
.bento-widget-icon {
  display: grid; place-items: center; width: 26px; height: 26px; border-radius: 7px; flex: none;
  background: color-mix(in srgb, #746294 16%, transparent);
  color: #9e86d0;
}
.bento-widget-title { font-size: 12.5px; font-weight: 700; color: var(--text-primary); flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.bento-root-badge {
  border: 0; border-radius: 999px; padding: 2px 7px; font-size: 10.5px; cursor: pointer;
  background: var(--surface-section); color: var(--text-secondary);
}
.bento-root-badge:hover { background: var(--surface-hover); color: var(--text-primary); }
.bento-ws-meta { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
.bento-ws-name { font-size: 12px; color: var(--text-primary); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.bento-ws-empty { font-size: 11.5px; color: var(--text-muted); }
.bento-ws-file {
  font-size: 11px; color: var(--text-secondary); background: var(--surface-section);
  padding: 2px 6px; border-radius: 5px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; align-self: flex-start; max-width: 100%;
}
.bento-widget-links { display: flex; flex-wrap: wrap; gap: 6px; padding-top: 2px; }
.bento-chip-btn {
  display: inline-flex; align-items: center; gap: 4px; padding: 4px 8px; border-radius: 7px;
  border: 1px solid var(--border-muted); background: var(--surface-section); color: var(--text-secondary);
  font-size: 11px; cursor: pointer; transition: background 140ms ease, color 140ms ease, border-color 140ms ease;
}
.bento-chip-btn:hover { background: var(--surface-hover); color: var(--text-primary); border-color: var(--accent-primary); }
</style>
