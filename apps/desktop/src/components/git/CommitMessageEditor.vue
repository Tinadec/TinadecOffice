<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { UiBadge } from '@/components/ui'

interface Props {
  modelValue?: string
  /** Recent commit messages (oneline) for reuse. */
  recentCommits?: string[]
}

const props = withDefaults(defineProps<Props>(), {
  modelValue: '',
  recentCommits: () => []
})

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()

const { t } = useI18n()

const COMMIT_TYPES = [
  'feat',
  'fix',
  'docs',
  'style',
  'refactor',
  'perf',
  'test',
  'build',
  'ci',
  'chore',
  'revert'
] as const

type CommitType = (typeof COMMIT_TYPES)[number]

const type = ref<CommitType>('feat')
const scope = ref('')
const subject = ref('')
const body = ref('')
const footer = ref('')
const typeMenuOpen = ref(false)

const SUBJECT_LIMIT = 50
const SUBJECT_HARD_LIMIT = 72

const subjectLength = computed(() => subject.value.length)
const subjectOverLimit = computed(() => subjectLength.value > SUBJECT_LIMIT)
const subjectOverHardLimit = computed(() => subjectLength.value > SUBJECT_HARD_LIMIT)

const assembledMessage = computed(() => {
  const header = buildHeader()
  const parts = [header]
  if (body.value.trim()) {
    parts.push('', body.value.trim())
  }
  if (footer.value.trim()) {
    parts.push('', footer.value.trim())
  }
  return parts.join('\n')
})

function buildHeader(): string {
  const scopePart = scope.value.trim() ? `(${scope.value.trim()})` : ''
  const subjectPart = subject.value.trim()
  return `${type.value}${scopePart}: ${subjectPart}`.trim()
}

function emitChange() {
  emit('update:modelValue', assembledMessage.value)
}

function selectType(value: CommitType) {
  type.value = value
  typeMenuOpen.value = false
  emitChange()
}

function reuseCommit(message: string) {
  parseAndApply(message)
}

function parseAndApply(message: string) {
  const lines = message.split(/\r?\n/)
  const header = lines[0] ?? ''
  const match = /^(\w+)(?:\(([^)]*)\))?!?\s*:\s*(.*)$/.exec(header)
  if (match) {
    const parsedType = match[1] as CommitType
    if ((COMMIT_TYPES as readonly string[]).includes(parsedType)) {
      type.value = parsedType
    }
    scope.value = match[2] ?? ''
    subject.value = match[3] ?? ''
  } else {
    subject.value = header
  }
  const rest = lines.slice(1)
  const trimmed = rest.map((line) => line).join('\n').replace(/^\n+/, '')
  const footerMatch = /^(BREAKING CHANGE:[\s\S]*|[\w-]+: .*)$/m
  if (footerMatch.test(trimmed)) {
    const idx = trimmed.search(footerMatch)
    if (idx >= 0) {
      body.value = trimmed.slice(0, idx).replace(/\n+$/, '')
      footer.value = trimmed.slice(idx)
    } else {
      body.value = trimmed
      footer.value = ''
    }
  } else {
    body.value = trimmed
    footer.value = ''
  }
  emitChange()
}

watch([type, scope, subject, body, footer], () => {
  emitChange()
})

watch(
  () => props.modelValue,
  (next) => {
    if (next === assembledMessage.value) return
    if (!next) {
      type.value = 'feat'
      scope.value = ''
      subject.value = ''
      body.value = ''
      footer.value = ''
    }
  }
)

emitChange()
</script>

<template>
  <div class="commit-editor">
    <div class="commit-editor-row">
      <div class="commit-editor-type">
        <label>{{ t('context.gitCommitType') }}</label>
        <div class="commit-editor-type-input">
          <button
            type="button"
            class="commit-editor-type-trigger"
            @click="typeMenuOpen = !typeMenuOpen"
          >
            <span>{{ type }}</span>
            <small>▾</small>
          </button>
          <div v-if="typeMenuOpen" class="commit-editor-type-menu">
            <button
              v-for="value in COMMIT_TYPES"
              :key="value"
              type="button"
              class="commit-editor-type-option"
              :class="{ active: value === type }"
              @click="selectType(value)"
            >
              {{ value }}
            </button>
          </div>
        </div>
      </div>

      <div class="commit-editor-scope">
        <label>{{ t('context.gitCommitScope') }}</label>
        <input
          v-model="scope"
          type="text"
          :placeholder="'api'"
        />
      </div>
    </div>

    <div class="commit-editor-subject">
      <label>
        <span>{{ t('context.gitCommitSubject') }}</span>
        <UiBadge
          :variant="subjectOverHardLimit ? 'destructive' : subjectOverLimit ? 'secondary' : 'outline'"
        >
          {{ subjectLength }}/{{ SUBJECT_LIMIT }}
        </UiBadge>
      </label>
      <input
        v-model="subject"
        type="text"
        :placeholder="t('context.gitCommitSubject')"
      />
      <small v-if="subjectOverLimit" class="commit-editor-hint">
        {{ t('context.gitCommitSubjectHint') }}
      </small>
    </div>

    <div class="commit-editor-body">
      <label>{{ t('context.gitCommitBody') }}</label>
      <textarea
        v-model="body"
        rows="3"
        :placeholder="t('context.gitCommitBody')"
      />
    </div>

    <div class="commit-editor-footer">
      <label>{{ t('context.gitCommitFooter') }}</label>
      <textarea
        v-model="footer"
        rows="2"
        :placeholder="'BREAKING CHANGE: ...'"
      />
    </div>

    <div class="commit-editor-preview">
      <label>{{ t('context.gitCommitPreview') }}</label>
      <pre>{{ assembledMessage }}</pre>
    </div>

    <div v-if="recentCommits.length > 0" class="commit-editor-history">
      <label>{{ t('context.gitCommitHistory') }}</label>
      <div class="commit-editor-history-list">
        <button
          v-for="(commit, index) in recentCommits.slice(0, 5)"
          :key="index"
          type="button"
          class="commit-editor-history-item"
          :title="commit"
          @click="reuseCommit(commit)"
        >
          <code>{{ commit }}</code>
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.commit-editor {
  display: grid;
  gap: 8px;
}

.commit-editor-row {
  display: grid;
  grid-template-columns: 140px 1fr;
  gap: 8px;
}

.commit-editor-subject,
.commit-editor-body,
.commit-editor-footer,
.commit-editor-preview,
.commit-editor-history,
.commit-editor-type,
.commit-editor-scope {
  display: grid;
  gap: 4px;
  min-width: 0;
}

.commit-editor label {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 700;
}

.commit-editor input,
.commit-editor textarea {
  width: 100%;
  padding: 6px 8px;
  color: var(--text-primary);
  background: var(--bg-primary);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  font-size: 12px;
  font-family: 'Geist Mono', ui-monospace, monospace;
}

.commit-editor textarea {
  resize: vertical;
  font-family: 'Geist Mono', ui-monospace, monospace;
}

.commit-editor input:focus,
.commit-editor textarea:focus {
  outline: none;
  border-color: var(--border-input-focus);
  box-shadow: var(--shadow-focus);
}

.commit-editor-type-input {
  position: relative;
}

.commit-editor-type-trigger {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  padding: 6px 8px;
  color: var(--text-primary);
  background: var(--bg-primary);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  font-size: 12px;
  font-family: 'Geist Mono', ui-monospace, monospace;
  cursor: pointer;
  text-align: left;
}

.commit-editor-type-trigger small {
  color: var(--text-muted);
}

.commit-editor-type-menu {
  position: absolute;
  z-index: 50;
  top: 100%;
  left: 0;
  right: 0;
  max-height: 220px;
  overflow: auto;
  padding: 4px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: var(--bg-popover, var(--bg-primary));
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.25);
}

.commit-editor-type-option {
  display: block;
  width: 100%;
  padding: 5px 8px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  font-family: 'Geist Mono', ui-monospace, monospace;
  text-align: left;
  cursor: pointer;
}

.commit-editor-type-option:hover,
.commit-editor-type-option.active {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.commit-editor-hint {
  color: var(--text-secondary);
  font-size: 10px;
}

.commit-editor-preview pre {
  margin: 0;
  padding: 8px;
  color: var(--text-primary);
  background: var(--bg-tertiary, var(--bg-secondary));
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  font-family: 'Geist Mono', ui-monospace, monospace;
  font-size: 11px;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 140px;
  overflow: auto;
}

.commit-editor-history-list {
  display: grid;
  gap: 4px;
  max-height: 120px;
  overflow: auto;
}

.commit-editor-history-item {
  display: block;
  width: 100%;
  padding: 5px 8px;
  border: 1px solid var(--border-muted);
  border-radius: 4px;
  background: var(--bg-primary);
  cursor: pointer;
  text-align: left;
}

.commit-editor-history-item:hover {
  background: var(--bg-hover);
}

.commit-editor-history-item code {
  display: block;
  color: var(--text-secondary);
  font-family: 'Geist Mono', ui-monospace, monospace;
  font-size: 11px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
</style>
