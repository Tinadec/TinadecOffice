<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { ToolExecutionPreviewDto } from '@/api'
import { displayPath } from '@/lib/conversation/toolSentence'

/**
 * What a finished call returned, as Core's bounded preview (APP-HOME-108 B2). Core already
 * cut, redacted and withheld it; this only lays it out. Code-like output is a code island;
 * nothing here is interactive.
 */
const props = defineProps<{ preview: ToolExecutionPreviewDto; workspaceRoot?: string | null }>()
const { t } = useI18n()

const body = computed(() => props.preview.preview)
const json = computed(() => (body.value?.kind === 'json' ? JSON.stringify(body.value.value ?? null, null, 2) : ''))
const path = (value?: string | null) => (value ? displayPath(value, props.workspaceRoot) : '')
const diffLines = computed(() => (body.value?.kind === 'text' ? (body.value.text ?? '').split('\n') : []))
</script>

<template>
  <div class="conversation-preview" data-testid="tool-preview" :data-kind="body?.kind ?? 'none'">
    <template v-if="!body" />
    <template v-else-if="body.kind === 'file_lines'">
      <p v-if="body.path" class="conversation-preview-caption">{{ path(body.path) }}</p>
      <pre class="conversation-code"><template v-for="line in body.lines ?? []" :key="line.line_number ?? line.text"><span class="conversation-code-gutter">{{ line.line_number }}</span>{{ line.text }}
</template></pre>
    </template>
    <template v-else-if="body.kind === 'search_matches'">
      <pre class="conversation-code"><template v-for="(line, index) in body.lines ?? []" :key="index"><span class="conversation-code-gutter">{{ path(line.path) }}:{{ line.line_number }}</span><span :class="{ 'is-context': line.is_match === false }">{{ line.text }}</span>
</template></pre>
    </template>
    <template v-else-if="body.kind === 'directory'">
      <ul class="conversation-preview-entries" role="list">
        <li v-for="entry in body.entries ?? []" :key="entry.name">{{ entry.name }}{{ entry.type === 'directory' ? '/' : '' }}</li>
      </ul>
    </template>
    <template v-else-if="body.kind === 'command_output'">
      <pre v-if="body.stdout" class="conversation-code">{{ body.stdout }}</pre>
      <pre v-if="body.stderr" class="conversation-code is-stderr">{{ body.stderr }}</pre>
      <p class="conversation-preview-caption">
        {{ body.timed_out ? t('conversation.tool.preview.timedOut') : t('conversation.tool.preview.exitCode', { code: body.exit_code ?? '?' }) }}
      </p>
    </template>
    <template v-else-if="body.kind === 'text'">
      <pre class="conversation-code" :data-language="body.language ?? undefined"><template v-for="(line, index) in diffLines" :key="index"><span :class="{ 'is-added': body.language === 'diff' && line.startsWith('+'), 'is-removed': body.language === 'diff' && line.startsWith('-') }">{{ line }}</span>
</template></pre>
    </template>
    <template v-else-if="body.kind === 'json'">
      <pre class="conversation-code">{{ json }}</pre>
    </template>
    <p v-else-if="body.kind === 'withheld'" class="conversation-preview-caption">
      {{ body.reason === 'sensitive_path' ? t('conversation.tool.preview.withheldSensitive') : t('conversation.tool.preview.withheld') }}
    </p>
    <p v-if="preview.truncated" class="conversation-preview-caption">{{ t('conversation.tool.preview.truncated') }}</p>
  </div>
</template>

<style scoped>
.conversation-preview { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
.conversation-preview-caption { margin: 0; font-size: 11px; color: var(--text-muted); }
.conversation-preview-entries { margin: 0; padding: 0; list-style: none; columns: 2; font-size: 12px; font-family: var(--font-mono, ui-monospace, monospace); color: var(--text-secondary); }
.conversation-code-gutter { display: inline-block; min-width: 2.5em; margin-right: 0.75em; color: var(--text-muted); user-select: none; text-align: right; }
.is-context { color: var(--text-muted); }
.is-added { color: var(--accent-success); }
.is-removed { color: var(--accent-danger); }
.is-stderr { color: var(--accent-danger); }
</style>
