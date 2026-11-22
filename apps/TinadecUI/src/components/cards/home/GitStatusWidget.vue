<script setup lang="ts">
import { computed, inject, toValue, type MaybeRefOrGetter } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowDown, ArrowRight, ArrowUp, GitBranch, GitCommit, RefreshCw } from '@lucide/vue'
import type { ProjectDto } from '@/api'
import { useHomeGitStatus } from '@/composables/useHomeGitStatus'
import { useConnection } from '@/composables/useConnection'
import { UiIslandCard } from '@/components/ui'

const props = defineProps<{ project: ProjectDto | null }>()
const emit = defineEmits<{
  open: []
  'open-file': [filePath: string]
  'open-tab': [tab: 'changes' | 'history' | 'branches']
}>()
const active = inject<MaybeRefOrGetter<boolean>>('uie:active', true)
const { t, te, locale } = useI18n()
const label = (key: string, fallback: string) => (te(key) ? String(t(key)) : fallback)
const { hostStatus } = useConnection()
const { summary, updatedAt, error, loading, busy, recentCommit, historyLoaded, historyError, businessReady, refresh } = useHomeGitStatus(
  () => props.project, () => toValue(active),
)
const stale = computed(() => !businessReady.value || !!error.value || (loading.value && !!summary.value))
const tone = computed(() => !stale.value && summary.value?.conflicts ? 'danger'
  : !stale.value && summary.value?.total ? 'attention' : 'neutral')
const headline = computed(() => {
  if (!props.project) return t('context.gitWidgetNoProject')
  if (!businessReady.value) return hostStatus.value.state === 'preview'
    ? t('context.gitWidgetPreview') : t('context.gitWidgetUnavailable')
  if (error.value === 'not_a_repo') return t('context.gitWidgetNotRepo')
  if (error.value) return t('context.gitWidgetReadFailed')
  if (!summary.value) return loading.value ? t('context.gitWidgetLoading') : t('context.gitWidgetUnknown')
  if (summary.value.conflicts) return t('context.gitWidgetConflicts', { count: summary.value.conflicts })
  if (summary.value.total) return t('context.gitWidgetChanges', { count: summary.value.total })
  return t('context.gitWidgetClean')
})
const lastRead = computed(() => updatedAt.value == null ? ''
  : t('context.gitWidgetLastRead', { time: new Date(updatedAt.value).toLocaleTimeString(locale.value, { hour: '2-digit', minute: '2-digit' }) }))
</script>

<template>
  <UiIslandCard variant="raised" padding="none" class="git-widget" :class="`tone-${tone}`">
    <div class="git-widget-inner">
      <div class="git-widget-header">
        <span class="git-widget-icon"><GitBranch :size="17" aria-hidden="true" /></span>
        <span class="git-widget-title">{{ t('context.homeGit') }}</span>
        <button type="button" class="git-widget-refresh" :disabled="!project || !businessReady || busy"
          :aria-label="t('context.gitWidgetRefresh')" :title="t('context.gitWidgetRefresh')" @click="refresh(true)">
          <RefreshCw :size="15" aria-hidden="true" />
        </button>
      </div>
      <div class="git-widget-repo" :title="summary?.gitRoot ?? project?.path ?? ''">
        <span class="git-widget-truncate">{{ t('context.gitWidgetPrimary') }} · {{ project?.name ?? '—' }}</span>
        <span v-if="summary && !stale" class="git-widget-branch">
          {{ summary.detached ? t('context.gitWidgetDetached') : summary.branch }}
        </span>
      </div>
      <div v-if="summary && !stale" class="git-widget-upstream" :title="summary.upstream ?? ''">
        {{ summary.upstream ? `→ ${summary.upstream}` : t('context.gitWidgetNoUpstream') }}
      </div>
      <div role="status" aria-live="polite" class="git-widget-status" :class="{ 'git-widget-status-stale': stale }">{{ headline }}</div>
      <template v-if="summary && !stale">
        <div class="git-widget-metrics" :title="t('context.gitWidgetOverlapHint')">
          <span>{{ t('context.gitWidgetStaged', { count: summary.staged }) }}</span>
          <span>{{ t('context.gitWidgetUnstaged', { count: summary.unstaged }) }}</span>
          <span>{{ t('context.gitWidgetUntracked', { count: summary.untracked }) }}</span>
          <span :class="{ 'git-widget-conflict': summary.conflicts }">{{ t('context.gitWidgetConflictCount', { count: summary.conflicts }) }}</span>
        </div>
        <div v-if="summary.files.length" class="git-widget-files">
          <span class="git-widget-section-title">{{ t('context.gitWidgetFileHeading') }}</span>
          <button
            v-for="file in summary.files.slice(0, 3)"
            :key="file.path"
            type="button"
            class="git-widget-file git-widget-file-btn"
            :title="file.path"
            @click="emit('open-file', file.path)"
          >
            <span class="git-widget-file-mark" :class="`kind-${file.kind}`">{{ file.mark }}</span>
            <span class="git-widget-truncate">{{ file.path }}</span>
          </button>
          <span v-if="summary.total > 3" class="git-widget-more">{{ t('context.gitWidgetMoreFiles', { count: summary.total - 3 }) }}</span>
        </div>
        <div class="git-widget-quick-links" role="group" :aria-label="t('context.homeGit')">
          <button type="button" class="git-widget-quick-link" data-testid="git-quick-changes" @click="emit('open-tab', 'changes')">
            {{ label('context.gitQuickChanges', 'Changes') }}
          </button>
          <button type="button" class="git-widget-quick-link" data-testid="git-quick-log" @click="emit('open-tab', 'history')">
            {{ label('context.gitQuickLog', 'Log') }}
          </button>
          <button type="button" class="git-widget-quick-link" data-testid="git-quick-branches" @click="emit('open-tab', 'branches')">
            {{ label('context.gitQuickBranches', 'Branches') }}
          </button>
        </div>
        <div class="git-widget-history">
          <span class="git-widget-section-title"><GitCommit :size="13" aria-hidden="true" />{{ t('context.gitWidgetLatestCommit') }}</span>
          <div v-if="recentCommit && !historyError" class="git-widget-commit" :title="recentCommit.subject">
            <code>{{ recentCommit.shortHash }}</code><span class="git-widget-truncate">{{ recentCommit.subject }}</span>
          </div>
          <span v-else class="git-widget-history-empty">
            {{ historyError ? t('context.gitWidgetHistoryFailed') : historyLoaded ? t('context.gitWidgetNoCommits') : t('context.gitWidgetHistoryLoading') }}
          </span>
        </div>
        <div v-if="summary.upstream" class="git-widget-tracking" :title="t('context.gitWidgetTrackingHint')">
          {{ t('context.gitWidgetLocalTracking') }} <span class="git-widget-sync"><ArrowUp :size="12" aria-hidden="true" />{{ summary.ahead }} <ArrowDown :size="12" aria-hidden="true" />{{ summary.behind }}</span>
        </div>
      </template>
      <div v-else-if="summary && stale" class="git-widget-details git-widget-previous">
        {{ t('context.gitWidgetPrevious') }} · {{ lastRead }}
      </div>
      <div v-else-if="error === 'not_a_repo'" class="git-widget-details">{{ t('context.gitWidgetPrimaryOnly') }}</div>
      <div class="git-widget-footer">
        <span v-if="!stale && summary" class="git-widget-timestamp">{{ lastRead }}</span>
        <span v-else class="git-widget-timestamp" />
        <button type="button" class="git-widget-open" @click="emit('open')">
          {{ t('context.gitWidgetOpen') }} <ArrowRight :size="14" aria-hidden="true" />
        </button>
      </div>
    </div>
  </UiIslandCard>
</template>

<style scoped>
.git-widget { width: 100%; min-width: 0; flex: none; container-type: inline-size; }
.git-widget-inner { display: flex; flex-direction: column; gap: 8px; min-width: 0; padding: 14px 15px 12px; }
.git-widget-header { display: flex; align-items: center; gap: 9px; min-width: 0; }
.git-widget-icon { display: grid; place-items: center; width: 30px; height: 30px; flex: none; border-radius: 8px; background: color-mix(in srgb, var(--accent-warning) 13%, transparent); color: var(--accent-warning); }
.git-widget-title { font-size: 13px; font-weight: 700; color: var(--text-primary); flex: 1; }
.git-widget-refresh, .git-widget-open { border: 0; background: transparent; color: var(--text-secondary); cursor: pointer; }
.git-widget-refresh { width: 30px; height: 30px; border-radius: 7px; display: grid; place-items: center; flex: none; }
.git-widget-refresh:hover:not(:disabled), .git-widget-open:hover { background: var(--surface-hover); color: var(--text-primary); }
.git-widget-refresh:disabled { opacity: .45; cursor: default; }
.git-widget-repo { display: flex; align-items: center; justify-content: space-between; gap: 12px; min-width: 0; font-size: 12px; color: var(--text-secondary); }
.git-widget-truncate { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
.git-widget-branch { font-size: 12px; font-weight: 600; color: var(--text-primary); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 48%; }
.git-widget-upstream { margin-top: -5px; font-size: 11px; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.git-widget-status { font-size: 17px; font-weight: 700; line-height: 1.3; color: var(--text-primary); }
.git-widget-status-stale { color: var(--text-secondary); }
.tone-attention .git-widget-status { color: var(--accent-warning); }
.tone-danger .git-widget-status { color: var(--accent-danger); }
.git-widget-details { display: flex; align-items: center; justify-content: space-between; min-width: 0; gap: 8px; font-size: 11px; color: var(--text-secondary); min-height: 15px; }
.git-widget-metrics { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 4px; font-size: 10px; color: var(--text-secondary); }
.git-widget-metrics span { padding: 5px 4px; border-radius: 6px; background: var(--surface-section); text-align: center; white-space: nowrap; }
.git-widget-metrics .git-widget-conflict { color: var(--accent-danger); }
.git-widget-files, .git-widget-history { display: flex; flex-direction: column; gap: 5px; padding-top: 7px; border-top: 1px solid var(--border-default); min-width: 0; }
.git-widget-section-title { display: flex; align-items: center; gap: 5px; font-size: 11px; color: var(--text-muted); }
.git-widget-file { display: flex; align-items: center; gap: 8px; min-width: 0; font-size: 11px; color: var(--text-secondary); line-height: 1.3; }
.git-widget-file-btn { width: 100%; border: 0; border-radius: 6px; padding: 3px 5px; background: transparent; cursor: pointer; text-align: left; }
.git-widget-file-btn:hover { background: var(--surface-hover); color: var(--text-primary); }
.git-widget-quick-links { display: flex; align-items: center; gap: 5px; flex-wrap: wrap; padding-top: 4px; }
.git-widget-quick-link { border: 1px solid var(--border-muted); border-radius: 6px; padding: 2px 7px; background: var(--surface-section); color: var(--text-secondary); font-size: 10.5px; cursor: pointer; }
.git-widget-quick-link:hover { background: var(--surface-hover); color: var(--text-primary); border-color: var(--accent-primary); }
.git-widget-file-mark { flex: none; width: 14px; font-weight: 700; color: var(--accent-warning); }
.git-widget-file-mark.kind-conflict { color: var(--accent-danger); }
.git-widget-file-mark.kind-untracked { color: var(--text-muted); }
.git-widget-more, .git-widget-history-empty { font-size: 11px; color: var(--text-muted); }
.git-widget-commit { display: flex; align-items: baseline; gap: 7px; min-width: 0; font-size: 11px; color: var(--text-secondary); }
.git-widget-commit code { flex: none; font-size: 10px; color: var(--text-muted); }
.git-widget-tracking { display: flex; justify-content: space-between; align-items: center; font-size: 10px; color: var(--text-muted); }
.git-widget-previous { color: var(--text-muted); }
.git-widget-sync { display: inline-flex; align-items: center; gap: 2px; flex: none; }
.git-widget-footer { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-top: 3px; }
.git-widget-timestamp { font-size: 10px; color: var(--text-muted); }
.git-widget-open { display: inline-flex; align-items: center; gap: 4px; border-radius: 6px; padding: 4px 2px 4px 6px; font-size: 11px; white-space: nowrap; }
.git-widget-refresh:focus-visible, .git-widget-open:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 2px; }
@container (max-width: 340px) { .git-widget-metrics { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (prefers-reduced-motion: reduce) { .git-widget * { transition: none; } }
</style>
