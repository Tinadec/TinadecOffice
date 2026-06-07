<script setup lang="ts">
import { AlertTriangle, CheckCircle2, GitBranch, RefreshCw, ShieldCheck } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, type CodeToolExecuteResultDto } from '../api'

const { t } = useI18n()

interface GitPushPlanData {
  branch?: string
  upstream?: string | null
  ahead?: number
  behind?: number
  has_uncommitted_changes?: boolean
  diff_stat?: string
  push_ready?: boolean
  push_blockers?: string[]
  suggested_commands?: string[]
}

const props = defineProps<{
  currentProjectPath?: string
}>()

const loading = ref(false)
const error = ref<string | null>(null)
const plan = ref<CodeToolExecuteResultDto | null>(null)

const planData = computed(() => (plan.value?.data ?? {}) as GitPushPlanData)
const pushBlockers = computed(() => Array.isArray(planData.value.push_blockers) ? planData.value.push_blockers : [])
const suggestedCommands = computed(() => Array.isArray(planData.value.suggested_commands) ? planData.value.suggested_commands : [])
const diffStatLines = computed(() =>
  typeof planData.value.diff_stat === 'string'
    ? planData.value.diff_stat.split(/\r?\n/).map((line) => line.trim()).filter(Boolean)
    : []
)
const pushReady = computed(() => planData.value.push_ready === true)

async function loadGitPlan() {
  if (!props.currentProjectPath) {
    plan.value = null
    error.value = null
    return
  }

  loading.value = true
  error.value = null
  try {
    plan.value = await api.executeCodeTool('git_worktree_manager', {
      cwd: props.currentProjectPath,
      arguments: { action: 'push_plan' }
    })
  } catch (err) {
    error.value = err instanceof Error ? err.message : t('context.gitPlanFailed')
  } finally {
    loading.value = false
  }
}

watch(() => props.currentProjectPath, () => {
  void loadGitPlan()
}, { immediate: true })
</script>

<template>
  <section class="panel">
    <div class="git-plan-head">
      <div class="panel-title">
        <GitBranch :size="15" />
        <span>{{ t('context.gitPushReadiness') }}</span>
      </div>
      <button
        class="icon-button"
        :title="t('context.refreshGitPlan')"
        :disabled="loading || !props.currentProjectPath"
        @click="loadGitPlan"
      >
        <RefreshCw :size="14" />
      </button>
    </div>

    <div v-if="!props.currentProjectPath" class="diff-box">
      <span>{{ t('context.diffPlaceholder') }}</span>
    </div>
    <div v-else-if="loading" class="diff-box">
      <span>{{ t('context.loadingGitPlan') }}</span>
    </div>
    <div v-else-if="error" class="git-plan-state risky">
      <AlertTriangle :size="15" />
      <span>{{ error }}</span>
    </div>
    <div v-else-if="plan" class="git-plan-card">
      <div class="git-plan-status" :class="{ ready: pushReady, blocked: !pushReady }">
        <component :is="pushReady ? CheckCircle2 : AlertTriangle" :size="18" />
        <div>
          <strong>{{ pushReady ? t('context.gitPushReady') : t('context.gitPushBlocked') }}</strong>
          <span>{{ plan.summary }}</span>
        </div>
      </div>

      <div class="git-plan-grid">
        <div>
          <span>{{ t('context.gitBranch') }}</span>
          <strong>{{ planData.branch ?? '-' }}</strong>
        </div>
        <div>
          <span>{{ t('context.gitUpstream') }}</span>
          <strong>{{ planData.upstream ?? '-' }}</strong>
        </div>
        <div>
          <span>{{ t('context.gitAheadBehind') }}</span>
          <strong>{{ planData.ahead ?? 0 }} / {{ planData.behind ?? 0 }}</strong>
        </div>
      </div>

      <div v-if="pushBlockers.length > 0" class="git-plan-section">
        <span>{{ t('context.gitBlockers') }}</span>
        <div class="git-plan-tags">
          <small v-for="blocker in pushBlockers" :key="blocker">{{ blocker }}</small>
        </div>
      </div>

      <div v-if="diffStatLines.length > 0" class="git-plan-section">
        <span>{{ t('context.gitDiffStat') }}</span>
        <pre>{{ diffStatLines.join('\n') }}</pre>
      </div>

      <div v-if="suggestedCommands.length > 0" class="git-plan-section">
        <span>{{ t('context.gitSuggestedCommands') }}</span>
        <code v-for="command in suggestedCommands" :key="command">{{ command }}</code>
      </div>

      <div class="git-plan-approval">
        <ShieldCheck :size="14" />
        <span>{{ t('context.gitPlanApproval') }}</span>
      </div>
    </div>
  </section>
</template>
