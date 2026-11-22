<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ExternalLink, Plus, RotateCcw, Search, Trash2 } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiSelectField, UiTextarea } from '@/components/ui'
import {
  COMPANION_MEMORY_SECTIONS,
  type MemoryDocumentScope,
  type MemoryKind,
  type MemoryLedgerItemDto,
} from '@/lib/contextMemoryApi'

const props = withDefaults(
  defineProps<{
    scope: MemoryDocumentScope
    items: MemoryLedgerItemDto[]
    loading?: boolean
    busyId?: string | null
    adding?: boolean
    disabled?: boolean
  }>(),
  {
    loading: false,
    busyId: null,
    adding: false,
    disabled: false,
  },
)

const emit = defineEmits<{
  add: [payload: { kind: MemoryKind; section?: string | null; content: string }]
  revert: [item: MemoryLedgerItemDto]
  revoke: [item: MemoryLedgerItemDto]
  'open-session': [sessionId: string]
}>()

const { t } = useI18n()

const searchQuery = ref('')
const tierFilter = ref<'all' | 'hot' | 'cold'>('all')
const addOpen = ref(false)
const addForm = reactive<{
  kind: MemoryKind
  section: string
  content: string
}>({
  kind: props.scope === 'principal' ? 'preference' : props.scope === 'companion' ? 'bond' : 'fact',
  section: COMPANION_MEMORY_SECTIONS[0]!.heading,
  content: '',
})

const kindOptions = computed(() => {
  const keys =
    props.scope === 'companion'
      ? (['bond', 'episode', 'task_template', 'fact', 'preference', 'supervision_rule'] as const)
      : props.scope === 'principal'
        ? (['preference', 'fact', 'decision', 'task_template'] as const)
        : (['fact', 'decision', 'success_pattern', 'failure_pattern', 'task_template', 'supervision_rule'] as const)
  return keys.map((key) => ({
    value: key,
    label: t(`memoryContext.kinds.${key}`),
  }))
})

const sectionOptions = computed(() =>
  COMPANION_MEMORY_SECTIONS.map((spec) => ({
    value: spec.heading,
    label: t(`memoryContext.companion.sections.${spec.key}`),
  })),
)

const filteredItems = computed(() => {
  const q = searchQuery.value.trim().toLowerCase()
  return props.items.filter((item) => {
    if (tierFilter.value === 'hot' && !item.in_core_markdown) return false
    if (tierFilter.value === 'cold' && item.in_core_markdown) return false
    if (!q) return true
    const haystack = [
      item.content,
      item.previous_content ?? '',
      item.kind,
      item.section ?? '',
      item.source_session_id ?? '',
      item.action,
    ]
      .join(' ')
      .toLowerCase()
    return haystack.includes(q)
  })
})

function formatTimestamp(iso: string): string {
  if (!iso) return ''
  const parsed = new Date(iso)
  return Number.isNaN(parsed.getTime()) ? iso : parsed.toLocaleString()
}

function shortSessionId(sessionId: string): string {
  return sessionId.length > 12 ? `${sessionId.slice(0, 8)}…` : sessionId
}

function actionLabel(action: string): string {
  return t(`memoryContext.ledger.action.${action}`)
}

function submitAdd() {
  const content = addForm.content.trim()
  if (!content || props.adding || props.disabled) return
  emit('add', {
    kind: addForm.kind,
    section: props.scope === 'companion' ? addForm.section : null,
    content,
  })
  addForm.content = ''
  addOpen.value = false
}
</script>

<template>
  <section class="mc-ledger-panel" :data-testid="`memory-ledger-${scope}`">
    <header class="mc-ledger-header">
      <div class="mc-ledger-title-group">
        <h4>{{ t('memoryContext.ledger.title') }}</h4>
        <UiBadge variant="outline">{{ filteredItems.length }}</UiBadge>
      </div>
      <UiButton
        size="sm"
        :variant="addOpen ? 'outline' : 'default'"
        :disabled="disabled"
        data-testid="memory-ledger-open-add-btn"
        @click="addOpen = !addOpen"
      >
        <Plus v-if="!addOpen" :size="14" aria-hidden="true" />
        <span>{{ addOpen ? t('memoryContext.ledger.cancelAdd') : t('memoryContext.ledger.addBtn') }}</span>
      </UiButton>
    </header>

    <form v-if="addOpen" class="mc-add-form" data-testid="memory-ledger-add-form" @submit.prevent="submitAdd">
      <div class="mc-add-meta-row">
        <UiSelectField
          v-model="addForm.kind"
          :options="kindOptions"
          :disabled="adding || disabled"
          data-testid="memory-add-kind-select"
        />
        <UiSelectField
          v-if="scope === 'companion'"
          v-model="addForm.section"
          :options="sectionOptions"
          :disabled="adding || disabled"
          data-testid="memory-add-section-select"
        />
      </div>
      <UiTextarea
        v-model="addForm.content"
        :rows="2"
        :disabled="adding || disabled"
        :placeholder="t('memoryContext.ledger.contentPlaceholder')"
        data-testid="memory-add-content-input"
      />
      <div class="mc-add-actions">
        <UiButton
          type="submit"
          size="sm"
          :disabled="adding || disabled || !addForm.content.trim()"
          data-testid="memory-add-submit-btn"
        >
          {{ t('memoryContext.ledger.submitAdd') }}
        </UiButton>
      </div>
    </form>

    <div class="mc-ledger-toolbar">
      <div class="mc-search-box">
        <Search :size="14" class="mc-search-icon" aria-hidden="true" />
        <UiInput
          v-model="searchQuery"
          :placeholder="t('memoryContext.ledger.searchPlaceholder')"
          data-testid="memory-ledger-search"
        />
      </div>
      <div class="mc-filter-pills" role="group">
        <button
          type="button"
          :class="['mc-filter-pill', { active: tierFilter === 'all' }]"
          @click="tierFilter = 'all'"
        >
          {{ t('memoryContext.ledger.filterAll') }}
        </button>
        <button
          type="button"
          :class="['mc-filter-pill', { active: tierFilter === 'hot' }]"
          @click="tierFilter = 'hot'"
        >
          {{ t('memoryContext.ledger.filterHot') }}
        </button>
        <button
          type="button"
          :class="['mc-filter-pill', { active: tierFilter === 'cold' }]"
          @click="tierFilter = 'cold'"
        >
          {{ t('memoryContext.ledger.filterCold') }}
        </button>
      </div>
    </div>

    <div v-if="filteredItems.length === 0 && !loading" class="mc-ledger-empty" data-testid="memory-ledger-empty">
      {{ t('memoryContext.ledger.empty') }}
    </div>

    <ul v-else class="mc-ledger-list">
      <li
        v-for="item in filteredItems"
        :key="item.id"
        :class="['mc-ledger-row', `is-${item.action}`, { 'is-revoked': item.status === 'revoked' || item.action === 'revoke' }]"
        :data-testid="`memory-ledger-item-${item.id}`"
      >
        <div class="mc-row-badges">
          <span :class="['mc-mutation-badge', `action-${item.action}`]" data-testid="memory-item-mutation-badge">
            {{ actionLabel(item.action) }}
          </span>
          <UiBadge :variant="item.in_core_markdown ? 'default' : 'outline'">
            {{ item.in_core_markdown ? t('memoryContext.ledger.hotBadge') : t('memoryContext.ledger.coldBadge') }}
          </UiBadge>
          <UiBadge variant="secondary">{{ item.kind }}</UiBadge>
          <UiBadge v-if="item.section" variant="outline">{{ item.section }}</UiBadge>
          <span class="mc-version-tag">v{{ item.version }}</span>
        </div>

        <p class="mc-row-content">{{ item.content }}</p>

        <p v-if="item.previous_content" class="mc-row-diff">
          {{ t('memoryContext.ledger.previousValue', { text: item.previous_content }) }}
        </p>
        <p v-if="item.revocation_reason" class="mc-row-revoked">
          {{ t('memoryContext.ledger.revocationReason', { reason: item.revocation_reason }) }}
        </p>

        <div class="mc-row-footer">
          <div class="mc-provenance">
            <button
              v-if="item.source_session_id"
              type="button"
              class="mc-session-link"
              data-testid="memory-item-provenance-link"
              @click="emit('open-session', item.source_session_id)"
            >
              <ExternalLink :size="11" aria-hidden="true" />
              <span>
                {{
                  t('memoryContext.ledger.provenanceSession', {
                    session: shortSessionId(item.source_session_id),
                    seq: item.source_message_seq != null ? ` #${item.source_message_seq}` : '',
                  })
                }}
              </span>
            </button>
            <span v-else class="mc-provenance-manual">
              {{ t('memoryContext.ledger.provenanceManual') }}
            </span>
            <span v-if="item.updated_at || item.created_at" class="mc-timestamp">
              {{ formatTimestamp(item.updated_at || item.created_at) }}
            </span>
          </div>

          <div class="mc-row-actions">
            <UiButton
              size="sm"
              variant="outline"
              :disabled="disabled || busyId === item.id"
              data-testid="memory-item-revert-btn"
              @click="emit('revert', item)"
            >
              <RotateCcw :size="12" aria-hidden="true" />
              <span>{{ t('memoryContext.ledger.revertBtn') }}</span>
            </UiButton>
            <UiButton
              v-if="item.status !== 'revoked' && item.action !== 'revoke'"
              size="sm"
              variant="ghost"
              :disabled="disabled || busyId === item.id"
              data-testid="memory-item-delete-btn"
              @click="emit('revoke', item)"
            >
              <Trash2 :size="12" aria-hidden="true" />
              <span>{{ t('memoryContext.ledger.deleteBtn') }}</span>
            </UiButton>
          </div>
        </div>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.mc-ledger-panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px;
  border-radius: 12px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  min-width: 0;
}

.mc-ledger-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.mc-ledger-title-group {
  display: flex;
  align-items: center;
  gap: 8px;
}

.mc-ledger-title-group h4 {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.mc-add-form {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.mc-add-meta-row {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 8px;
}

.mc-add-actions {
  display: flex;
  justify-content: flex-end;
}

.mc-ledger-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.mc-search-box {
  position: relative;
  flex: 1 1 200px;
  display: flex;
  align-items: center;
}

.mc-search-icon {
  position: absolute;
  left: 10px;
  color: var(--text-muted);
  pointer-events: none;
}

.mc-search-box :deep(input) {
  padding-left: 30px;
}

.mc-filter-pills {
  display: inline-flex;
  gap: 3px;
  padding: 2px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-input);
}

.mc-filter-pill {
  padding: 4px 8px;
  border: 0;
  border-radius: var(--radius-control, 6px);
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  white-space: nowrap;
  flex-shrink: 0;
  cursor: pointer;
}

.mc-filter-pill:focus-visible,
.mc-session-link:focus-visible {
  outline: var(--focus-ring-width, 2px) solid var(--focus-ring-color, var(--accent-primary));
  outline-offset: 2px;
}

.mc-filter-pill.active {
  background: var(--surface-selected);
  color: var(--accent-primary);
  font-weight: 600;
}

.mc-ledger-empty {
  padding: 20px 12px;
  text-align: center;
  font-size: 12px;
  color: var(--text-muted);
}

.mc-ledger-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-height: 520px;
  overflow-y: auto;
}

.mc-ledger-row {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px 12px;
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.mc-ledger-row.is-revoked {
  opacity: 0.72;
}

.mc-row-badges {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.mc-mutation-badge {
  display: inline-flex;
  align-items: center;
  padding: 2px 7px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
  font-family: var(--font-mono);
}

.mc-mutation-badge.action-add {
  background: color-mix(in srgb, var(--accent-success) 16%, transparent);
  color: var(--accent-success);
}

.mc-mutation-badge.action-supersede {
  background: color-mix(in srgb, var(--accent-primary) 16%, transparent);
  color: var(--accent-primary);
}

.mc-mutation-badge.action-revoke,
.mc-mutation-badge.action-expire {
  background: color-mix(in srgb, var(--accent-danger) 16%, transparent);
  color: var(--accent-danger);
}

.mc-version-tag {
  margin-left: auto;
  font-family: var(--font-mono);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
  color: var(--text-muted);
}

.mc-row-content {
  margin: 0;
  font-size: 13px;
  line-height: 1.55;
  color: var(--text-primary);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.mc-row-diff,
.mc-row-revoked {
  margin: 0;
  padding: 4px 8px;
  border-radius: 6px;
  background: var(--surface-input);
  font-size: 11px;
  color: var(--text-secondary);
}

.mc-row-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
  padding-top: 2px;
}

.mc-provenance {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  font-size: 11px;
  color: var(--text-muted);
}

.mc-session-link {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  gap: 4px;
  padding: 2px 6px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-muted);
  background: var(--surface-input);
  color: var(--accent-primary);
  font-family: var(--font-mono);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
  cursor: pointer;
}

.mc-session-link:hover {
  background: var(--surface-hover);
}

.mc-provenance-manual,
.mc-timestamp {
  font-size: 11px;
  color: var(--text-muted);
}

.mc-row-actions {
  display: flex;
  align-items: center;
  gap: 6px;
}
</style>
