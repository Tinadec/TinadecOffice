<script setup lang="ts">
import {
  AlertTriangle,
  CheckCircle2,
  GitBranch as GitBranchIcon,
  GitCommit,
  GitMerge,
  GitPullRequest,
  Loader2,
  MoreVertical,
  Pencil,
  Plus,
  RefreshCw,
  ShieldCheck,
  ShieldX,
  Star,
  Trash2,
} from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { ApprovalDto } from '../../api'
import { UiBadge, UiButton, UiInput, UiScrollArea } from '../ui'
import CommitCompare from './CommitCompare.vue'
import WorktreeManager from './WorktreeManager.vue'

const props = defineProps<{
  cwd?: string
  currentBranch: string
  branches: Array<{
    name: string
    is_current: boolean
    is_remote: boolean
    upstream?: string | null
    ahead?: number
    behind?: number
    last_subject?: string
    last_date?: string
  }>
  checkoutApproval: ApprovalDto | null
  branchApproval: ApprovalDto | null
  fetchApproval: ApprovalDto | null
  deleteBranchApproval: ApprovalDto | null
  renameBranchApproval: ApprovalDto | null
  mergeApproval: ApprovalDto | null
  rebaseApproval: ApprovalDto | null
  worktreeApproval: ApprovalDto | null
  canDecideCheckoutApproval: boolean
  canDecideBranchApproval: boolean
  canDecideFetchApproval: boolean
  canDecideDeleteBranchApproval: boolean
  canDecideRenameBranchApproval: boolean
  canDecideMergeApproval: boolean
  canDecideRebaseApproval: boolean
  canDecideWorktreeApproval: boolean
  canRequestFetchApproval: boolean
  operationLoading: boolean
}>()

const emit = defineEmits<{
  'checkout': [branch: string]
  'create-branch': [name: string]
  'fetch': []
  'delete-branch': [payload: { branch: string; force: boolean }]
  'rename-branch': [newName: string]
  'merge-branch': [branch: string]
  'rebase-branch': [branch: string]
  'decide-approval': [approval: ApprovalDto, decision: 'approved' | 'rejected']
  'execute-checkout': []
  'execute-create-branch': []
  'execute-fetch': []
  'execute-delete-branch': []
  'execute-rename-branch': []
  'execute-merge': []
  'execute-rebase': [operation: 'continue' | 'abort' | 'skip']
  'create-worktree': [payload: { branch: string; path: string }]
  'remove-worktree': [path: string]
  'execute-worktree': []
}>()

const { t } = useI18n()

// ---- State ----
const loading = ref(false)
const error = ref<string | null>(null)
const filterText = ref('')
const showCreateForm = ref(false)
const newBranchName = ref('')
const activeSubview = ref<'branches' | 'worktrees' | 'compare'>('branches')
const menuBranch = ref<string | null>(null)
const renameName = ref('')
const showRenameForm = ref(false)

// ---- Computed ----
const filteredBranches = computed(() => {
  if (!filterText.value.trim()) return props.branches
  const lower = filterText.value.toLowerCase()
  return props.branches.filter((b) => b.name.toLowerCase().includes(lower))
})

const localBranches = computed(() => filteredBranches.value.filter((b) => !b.is_remote))
const remoteBranches = computed(() => filteredBranches.value.filter((b) => b.is_remote))

const canCreateBranch = computed(() =>
  Boolean(props.cwd && newBranchName.value.trim() && !props.operationLoading),
)

// ---- Actions ----
function handleCheckout(branch: string) {
  if (branch === props.currentBranch) return
  emit('checkout', branch)
}

function handleCreateBranch() {
  if (!newBranchName.value.trim()) return
  emit('create-branch', newBranchName.value.trim())
  newBranchName.value = ''
  showCreateForm.value = false
}

function openBranchMenu(branch: string) {
  menuBranch.value = menuBranch.value === branch ? null : branch
}

function handleDeleteBranch(branch: string, force: boolean) {
  emit('delete-branch', { branch, force })
  menuBranch.value = null
}

function startRenameBranch(branch: string) {
  renameName.value = branch
  showRenameForm.value = true
  menuBranch.value = null
}

function handleRenameBranch() {
  if (!renameName.value.trim() || renameName.value.trim() === props.currentBranch) {
    showRenameForm.value = false
    return
  }
  emit('rename-branch', renameName.value.trim())
  showRenameForm.value = false
}

function handleMergeBranch(branch: string) {
  emit('merge-branch', branch)
  menuBranch.value = null
}

function handleRebaseBranch(branch: string) {
  emit('rebase-branch', branch)
  menuBranch.value = null
}

function onSwitchWorktree(path: string) {
  // Delegate to parent
}

function onCreateWorktree(payload: { branch: string; path: string }) {
  emit('create-worktree', payload)
}

function onRemoveWorktree(path: string) {
  emit('remove-worktree', path)
}

function refreshBranches() {
  emit('fetch')
}

defineExpose({ refresh: refreshBranches })
</script>

<template>
  <div class="git-branch-view">
    <!-- Sub-tabs -->
    <div class="git-branch-subtabs">
      <button
        class="git-branch-subtab"
        :class="{ active: activeSubview === 'branches' }"
        @click="activeSubview = 'branches'"
      >
        <GitBranchIcon :size="13" />
        <span>{{ t('context.gitBranches') }}</span>
      </button>
      <button
        class="git-branch-subtab"
        :class="{ active: activeSubview === 'worktrees' }"
        @click="activeSubview = 'worktrees'"
      >
        <GitPullRequest :size="13" />
        <span>{{ t('context.gitTabWorktrees') }}</span>
      </button>
      <button
        class="git-branch-subtab"
        :class="{ active: activeSubview === 'compare' }"
        @click="activeSubview = 'compare'"
      >
        <span>{{ t('context.gitTabCompare') }}</span>
      </button>
    </div>

    <!-- Branches sub-view -->
    <div v-if="activeSubview === 'branches'" class="git-branch-list-view">
      <div class="git-branch-toolbar">
        <UiInput
          v-model="filterText"
          :placeholder="t('context.gitBranchFilter')"
          size="sm"
        />
        <UiButton variant="ghost" size="xs" :disabled="!canRequestFetchApproval || operationLoading" @click="emit('fetch')">
          <RefreshCw :size="12" :class="{ spinning: operationLoading }" />
        </UiButton>
        <UiButton variant="secondary" size="xs" @click="showCreateForm = !showCreateForm">
          <Plus :size="12" />
          <span>{{ t('context.gitNewBranch') }}</span>
        </UiButton>
      </div>

      <!-- Create form -->
      <div v-if="showCreateForm" class="git-branch-create-form">
        <UiInput
          v-model="newBranchName"
          :placeholder="t('context.gitNewBranchPlaceholder')"
          size="sm"
          @keyup.enter="handleCreateBranch"
        />
        <div class="git-branch-create-actions">
          <UiButton
            variant="secondary"
            size="sm"
            :disabled="!canCreateBranch"
            @click="handleCreateBranch"
          >
            <ShieldCheck :size="13" />
            <span>{{ t('context.gitRequestApproval') }}</span>
          </UiButton>
          <UiButton variant="ghost" size="sm" @click="showCreateForm = false">
            {{ t('context.gitCompareCancel') }}
          </UiButton>
        </div>
        <!-- Execute approved -->
        <div v-if="branchApproval?.status === 'approved'" class="git-branch-execute-row">
          <UiButton variant="secondary" size="sm" :disabled="operationLoading" @click="emit('execute-create-branch')">
            <CheckCircle2 :size="13" />
            <span>{{ t('context.gitExecuteCreateBranch') }}</span>
          </UiButton>
        </div>
        <div v-if="canDecideBranchApproval" class="git-approval-decide">
          <button class="icon-button approve" :title="t('approval.approve')" @click="emit('decide-approval', branchApproval!, 'approved')">
            <CheckCircle2 :size="14" />
          </button>
          <button class="icon-button reject" :title="t('approval.reject')" @click="emit('decide-approval', branchApproval!, 'rejected')">
            <ShieldX :size="14" />
          </button>
        </div>
      </div>

      <!-- Rename form -->
      <div v-if="showRenameForm" class="git-branch-create-form">
        <UiInput
          v-model="renameName"
          :placeholder="t('context.gitNewBranchPlaceholder')"
          size="sm"
          @keyup.enter="handleRenameBranch"
        />
        <div class="git-branch-create-actions">
          <UiButton
            variant="secondary"
            size="sm"
            :disabled="!renameName.trim() || renameName.trim() === currentBranch"
            @click="handleRenameBranch"
          >
            <ShieldCheck :size="13" />
            <span>{{ t('context.gitRequestApproval') }}</span>
          </UiButton>
          <UiButton variant="ghost" size="sm" @click="showRenameForm = false">
            {{ t('context.gitCompareCancel') }}
          </UiButton>
        </div>
      </div>

      <!-- Error -->
      <div v-if="error" class="git-branch-error">
        <AlertTriangle :size="13" />
        <span>{{ error }}</span>
      </div>

      <!-- Loading -->
      <div v-if="loading && branches.length === 0" class="git-branch-loading">
        <Loader2 :size="18" class="spinning" />
        <span>{{ t('context.loadingGitPlan') }}</span>
      </div>

      <!-- Branch list -->
      <UiScrollArea v-else class="git-branch-scroll">
        <div v-if="localBranches.length > 0" class="git-branch-group">
          <div class="git-branch-group-title">{{ t('context.gitLocalBranches') }}</div>
          <div
            v-for="branch in localBranches"
            :key="branch.name"
            class="git-branch-row"
            :class="{ current: branch.is_current, 'menu-open': menuBranch === branch.name }"
          >
            <button class="git-branch-row-main" @click="handleCheckout(branch.name)">
              <div class="git-branch-row-icon">
                <GitBranchIcon :size="13" />
                <Star v-if="branch.is_current" :size="10" class="git-branch-star" />
              </div>
              <div class="git-branch-row-body">
                <span class="git-branch-row-name">{{ branch.name }}</span>
                <span v-if="branch.last_subject" class="git-branch-row-last">
                  {{ branch.last_subject }}
                </span>
              </div>
              <div class="git-branch-row-meta">
                <UiBadge v-if="(branch.ahead ?? 0) > 0 || (branch.behind ?? 0) > 0" variant="outline" class="git-branch-track">
                  ↑{{ branch.ahead ?? 0 }} ↓{{ branch.behind ?? 0 }}
                </UiBadge>
                <UiBadge v-if="branch.is_current" variant="secondary">{{ t('context.gitWorktreeCurrent') }}</UiBadge>
              </div>
            </button>
            <button
              class="git-branch-menu-toggle"
              :title="t('context.gitBranchActions')"
              @click.stop="openBranchMenu(branch.name)"
            >
              <MoreVertical :size="12" />
            </button>
            <div v-if="menuBranch === branch.name" class="git-branch-menu">
              <button v-if="!branch.is_current" class="git-branch-menu-item" @click="handleMergeBranch(branch.name)">
                <GitMerge :size="12" />
                <span>{{ t('context.gitMergeBranch') }}</span>
              </button>
              <button v-if="!branch.is_current" class="git-branch-menu-item" @click="handleRebaseBranch(branch.name)">
                <GitCommit :size="12" />
                <span>{{ t('context.gitRebaseBranch') }}</span>
              </button>
              <button class="git-branch-menu-item" @click="startRenameBranch(branch.name)">
                <Pencil :size="12" />
                <span>{{ t('context.gitRenameBranch') }}</span>
              </button>
              <button
                class="git-branch-menu-item danger"
                :disabled="branch.is_current"
                @click="handleDeleteBranch(branch.name, false)"
              >
                <Trash2 :size="12" />
                <span>{{ t('context.gitDeleteBranch') }}</span>
              </button>
              <button
                v-if="!branch.is_current"
                class="git-branch-menu-item danger"
                @click="handleDeleteBranch(branch.name, true)"
              >
                <Trash2 :size="12" />
                <span>{{ t('context.gitForceDeleteBranch') }}</span>
              </button>
            </div>
          </div>
        </div>

        <div v-if="remoteBranches.length > 0" class="git-branch-group">
          <div class="git-branch-group-title">{{ t('context.gitRemoteBranches') }}</div>
          <button
            v-for="branch in remoteBranches"
            :key="branch.name"
            class="git-branch-row"
            :class="{ current: branch.is_current }"
            @click="handleCheckout(branch.name)"
          >
            <GitBranchIcon :size="13" />
            <div class="git-branch-row-body">
              <span class="git-branch-row-name">{{ branch.name }}</span>
              <span v-if="branch.last_subject" class="git-branch-row-last">
                {{ branch.last_subject }}
              </span>
            </div>
          </button>
        </div>

        <div v-if="branches.length === 0 && !loading" class="git-branch-empty">
          {{ t('context.gitNoBranches') }}
        </div>
      </UiScrollArea>

      <!-- Fetch approval -->
      <div v-if="fetchApproval" class="git-branch-checkout-approval">
        <div class="git-branch-checkout-approval-info">
          <ShieldCheck :size="13" />
          <span>{{ fetchApproval.summary }}</span>
          <UiBadge :variant="fetchApproval.status === 'approved' ? 'default' : 'secondary'">
            {{ fetchApproval.status }}
          </UiBadge>
        </div>
        <div v-if="canDecideFetchApproval" class="git-approval-decide">
          <button class="icon-button approve" :title="t('approval.approve')" @click="emit('decide-approval', fetchApproval!, 'approved')">
            <CheckCircle2 :size="14" />
          </button>
          <button class="icon-button reject" :title="t('approval.reject')" @click="emit('decide-approval', fetchApproval!, 'rejected')">
            <ShieldX :size="14" />
          </button>
        </div>
        <UiButton
          v-if="fetchApproval.status === 'approved'"
          variant="secondary"
          size="sm"
          :disabled="operationLoading"
          @click="emit('execute-fetch')"
        >
          <CheckCircle2 :size="13" />
          <span>{{ t('context.gitExecuteFetch') }}</span>
        </UiButton>
      </div>

      <!-- Checkout approval -->
      <div v-if="checkoutApproval" class="git-branch-checkout-approval">
        <div class="git-branch-checkout-approval-info">
          <ShieldCheck :size="13" />
          <span>{{ checkoutApproval.summary }}</span>
          <UiBadge :variant="checkoutApproval.status === 'approved' ? 'default' : 'secondary'">
            {{ checkoutApproval.status }}
          </UiBadge>
        </div>
        <div v-if="canDecideCheckoutApproval" class="git-approval-decide">
          <button class="icon-button approve" :title="t('approval.approve')" @click="emit('decide-approval', checkoutApproval!, 'approved')">
            <CheckCircle2 :size="14" />
          </button>
          <button class="icon-button reject" :title="t('approval.reject')" @click="emit('decide-approval', checkoutApproval!, 'rejected')">
            <ShieldX :size="14" />
          </button>
        </div>
        <UiButton
          v-if="checkoutApproval.status === 'approved'"
          variant="secondary"
          size="sm"
          :disabled="operationLoading"
          @click="emit('execute-checkout')"
        >
          <CheckCircle2 :size="13" />
          <span>{{ t('context.gitExecuteCheckout') }}</span>
        </UiButton>
      </div>

      <!-- Merge approval -->
      <div v-if="mergeApproval" class="git-branch-checkout-approval">
        <div class="git-branch-checkout-approval-info">
          <ShieldCheck :size="13" />
          <span>{{ mergeApproval.summary }}</span>
          <UiBadge :variant="mergeApproval.status === 'approved' ? 'default' : 'secondary'">
            {{ mergeApproval.status }}
          </UiBadge>
        </div>
        <div v-if="canDecideMergeApproval" class="git-approval-decide">
          <button class="icon-button approve" :title="t('approval.approve')" @click="emit('decide-approval', mergeApproval!, 'approved')">
            <CheckCircle2 :size="14" />
          </button>
          <button class="icon-button reject" :title="t('approval.reject')" @click="emit('decide-approval', mergeApproval!, 'rejected')">
            <ShieldX :size="14" />
          </button>
        </div>
        <UiButton
          v-if="mergeApproval.status === 'approved'"
          variant="secondary"
          size="sm"
          :disabled="operationLoading"
          @click="emit('execute-merge')"
        >
          <CheckCircle2 :size="13" />
          <span>{{ t('context.gitExecuteMerge') }}</span>
        </UiButton>
      </div>

      <!-- Rebase approval -->
      <div v-if="rebaseApproval" class="git-branch-checkout-approval">
        <div class="git-branch-checkout-approval-info">
          <ShieldCheck :size="13" />
          <span>{{ rebaseApproval.summary }}</span>
          <UiBadge :variant="rebaseApproval.status === 'approved' ? 'default' : 'secondary'">
            {{ rebaseApproval.status }}
          </UiBadge>
        </div>
        <div v-if="canDecideRebaseApproval" class="git-approval-decide">
          <button class="icon-button approve" :title="t('approval.approve')" @click="emit('decide-approval', rebaseApproval!, 'approved')">
            <CheckCircle2 :size="14" />
          </button>
          <button class="icon-button reject" :title="t('approval.reject')" @click="emit('decide-approval', rebaseApproval!, 'rejected')">
            <ShieldX :size="14" />
          </button>
        </div>
        <UiButton
          v-if="rebaseApproval.status === 'approved'"
          variant="secondary"
          size="sm"
          :disabled="operationLoading"
          @click="emit('execute-rebase', 'continue')"
        >
          <CheckCircle2 :size="13" />
          <span>{{ t('context.gitExecuteRebase') }}</span>
        </UiButton>
      </div>

      <!-- Delete branch approval -->
      <div v-if="deleteBranchApproval" class="git-branch-checkout-approval">
        <div class="git-branch-checkout-approval-info">
          <ShieldCheck :size="13" />
          <span>{{ deleteBranchApproval.summary }}</span>
          <UiBadge :variant="deleteBranchApproval.status === 'approved' ? 'default' : 'secondary'">
            {{ deleteBranchApproval.status }}
          </UiBadge>
        </div>
        <div v-if="canDecideDeleteBranchApproval" class="git-approval-decide">
          <button class="icon-button approve" :title="t('approval.approve')" @click="emit('decide-approval', deleteBranchApproval!, 'approved')">
            <CheckCircle2 :size="14" />
          </button>
          <button class="icon-button reject" :title="t('approval.reject')" @click="emit('decide-approval', deleteBranchApproval!, 'rejected')">
            <ShieldX :size="14" />
          </button>
        </div>
        <UiButton
          v-if="deleteBranchApproval.status === 'approved'"
          variant="secondary"
          size="sm"
          :disabled="operationLoading"
          @click="emit('execute-delete-branch')"
        >
          <CheckCircle2 :size="13" />
          <span>{{ t('context.gitExecuteDeleteBranch') }}</span>
        </UiButton>
      </div>

      <!-- Rename branch approval -->
      <div v-if="renameBranchApproval" class="git-branch-checkout-approval">
        <div class="git-branch-checkout-approval-info">
          <ShieldCheck :size="13" />
          <span>{{ renameBranchApproval.summary }}</span>
          <UiBadge :variant="renameBranchApproval.status === 'approved' ? 'default' : 'secondary'">
            {{ renameBranchApproval.status }}
          </UiBadge>
        </div>
        <div v-if="canDecideRenameBranchApproval" class="git-approval-decide">
          <button class="icon-button approve" :title="t('approval.approve')" @click="emit('decide-approval', renameBranchApproval!, 'approved')">
            <CheckCircle2 :size="14" />
          </button>
          <button class="icon-button reject" :title="t('approval.reject')" @click="emit('decide-approval', renameBranchApproval!, 'rejected')">
            <ShieldX :size="14" />
          </button>
        </div>
        <UiButton
          v-if="renameBranchApproval.status === 'approved'"
          variant="secondary"
          size="sm"
          :disabled="operationLoading"
          @click="emit('execute-rename-branch')"
        >
          <CheckCircle2 :size="13" />
          <span>{{ t('context.gitExecuteRenameBranch') }}</span>
        </UiButton>
      </div>
    </div>

    <!-- Worktrees sub-view -->
    <div v-else-if="activeSubview === 'worktrees' && cwd">
      <WorktreeManager
        :cwd="cwd"
        :current-path="cwd"
        @switch="onSwitchWorktree"
        @create="onCreateWorktree"
        @remove="onRemoveWorktree"
      />
      <div v-if="worktreeApproval" class="git-branch-checkout-approval">
        <div class="git-branch-checkout-approval-info">
          <ShieldCheck :size="14" />
          <span>{{ worktreeApproval.summary }}</span>
          <UiBadge :variant="worktreeApproval.status === 'approved' ? 'default' : 'secondary'">{{ worktreeApproval.status }}</UiBadge>
        </div>
        <div v-if="canDecideWorktreeApproval" class="git-approval-decide">
          <button class="icon-button approve" :title="t('approval.approve')" @click="emit('decide-approval', worktreeApproval, 'approved')"><ShieldCheck :size="13" /></button>
          <button class="icon-button reject" :title="t('approval.reject')" @click="emit('decide-approval', worktreeApproval, 'rejected')"><ShieldX :size="13" /></button>
        </div>
        <UiButton v-if="worktreeApproval.status === 'approved'" variant="secondary" size="sm" :disabled="operationLoading" @click="emit('execute-worktree')">
          <CheckCircle2 :size="13" />
          <span>{{ t('context.gitWorktreeExecute') }}</span>
        </UiButton>
      </div>
    </div>

    <!-- Compare sub-view -->
    <CommitCompare
      v-else-if="activeSubview === 'compare' && cwd"
      :cwd="cwd"
    />
  </div>
</template>

<style scoped>
.git-branch-view {
  display: flex;
  flex-direction: column;
  gap: 8px;
  height: 100%;
  min-height: 0;
}

.git-branch-subtabs {
  display: flex;
  gap: 4px;
  flex-wrap: wrap;
}

.git-branch-subtab {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 5px 10px;
  background: var(--bg-secondary);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
  transition: all 0.12s;
}

.git-branch-subtab:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.git-branch-subtab.active {
  color: var(--text-primary);
  border-color: var(--bg-selected-outline);
  background: var(--bg-selected);
}

.git-branch-list-view {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-height: 0;
  flex: 1;
}

.git-branch-toolbar {
  display: flex;
  gap: 6px;
  align-items: center;
}

.git-branch-create-form {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-secondary);
}

.git-branch-create-actions {
  display: flex;
  gap: 6px;
}

.git-branch-execute-row {
  display: flex;
  gap: 6px;
}

.git-branch-error {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  color: var(--text-reject, #f85149);
  background: var(--bg-status-warn);
  border: 1px solid rgba(248, 81, 73, 0.25);
  border-radius: 6px;
  font-size: 12px;
}

.git-branch-loading {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 16px;
  color: var(--text-muted);
  font-size: 12px;
}

.git-branch-scroll {
  flex: 1;
  min-height: 200px;
}

.git-branch-group {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.git-branch-group-title {
  padding: 6px 8px 2px;
  font-size: 10px;
  font-weight: 700;
  color: var(--text-muted);
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.git-branch-row {
  position: relative;
  display: flex;
  align-items: flex-start;
  gap: 2px;
  padding: 2px 4px 2px 2px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  text-align: left;
  transition: background 0.1s;
}

.git-branch-row:hover,
.git-branch-row.menu-open {
  background: var(--bg-hover);
}

.git-branch-row.current {
  background: var(--bg-selected);
}

.git-branch-row-main {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: 1;
  min-width: 0;
  padding: 4px 6px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: inherit;
  cursor: pointer;
  text-align: left;
}

.git-branch-menu-toggle {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  padding: 0;
  margin-top: 2px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
  flex-shrink: 0;
}

.git-branch-menu-toggle:hover {
  background: var(--bg-tertiary);
  color: var(--text-primary);
}

.git-branch-menu {
  position: absolute;
  top: calc(100% + 2px);
  right: 4px;
  z-index: 10;
  display: flex;
  flex-direction: column;
  min-width: 150px;
  padding: 4px;
  background: var(--bg-primary);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
}

.git-branch-menu-item {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 5px 8px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--text-primary);
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
  text-align: left;
}

.git-branch-menu-item:hover:not(:disabled) {
  background: var(--bg-hover);
}

.git-branch-menu-item.danger {
  color: var(--text-reject, #f85149);
}

.git-branch-menu-item.danger:hover:not(:disabled) {
  background: rgba(248, 81, 73, 0.1);
}

.git-branch-menu-item:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.git-branch-row-icon {
  position: relative;
  display: flex;
  align-items: center;
  color: var(--text-muted);
}

.git-branch-row.current .git-branch-row-icon {
  color: var(--accent-primary);
}

.git-branch-star {
  position: absolute;
  top: -4px;
  right: -4px;
  color: #d29922;
}

.git-branch-row-body {
  display: flex;
  flex-direction: column;
  gap: 1px;
  flex: 1;
  min-width: 0;
}

.git-branch-row-name {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
  font-family: 'Geist Mono', ui-monospace, monospace;
}

.git-branch-row-last {
  font-size: 10px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.git-branch-row-meta {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
}

.git-branch-track {
  font-size: 10px;
  font-family: 'Geist Mono', ui-monospace, monospace;
}

.git-branch-empty {
  padding: 24px 16px;
  text-align: center;
  color: var(--text-muted);
  font-size: 12px;
}

.git-branch-checkout-approval {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-secondary);
}

.git-branch-checkout-approval-info {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-primary);
}

.git-approval-decide {
  display: flex;
  gap: 4px;
}

.spinning {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}
</style>
