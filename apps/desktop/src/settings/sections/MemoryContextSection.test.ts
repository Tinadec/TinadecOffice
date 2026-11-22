// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import { setHostAccessStatus } from '@/lib/hostAccess'
import { pendingSettingsSection } from '@/lib/pageRequests'
import { readCenterUiState } from '@/settings/drafts/uiState'

const mocks = vi.hoisted(() => ({
  listProjects: vi.fn(),
  listCompanions: vi.fn(),
  notify: {
    success: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
    warning: vi.fn(),
  },
  confirm: vi.fn().mockResolvedValue(true),
}))

vi.mock('@/api', () => ({
  api: {
    listProjects: mocks.listProjects,
  },
}))

vi.mock('@/lib/storageScope', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/storageScope')>()
  return {
    ...actual,
    scopedApi: (target: unknown) => target,
  }
})

vi.mock('@/lib/companionApi', () => ({
  companionApi: {
    listCompanions: mocks.listCompanions,
  },
}))

vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({
    notify: mocks.notify,
    confirm: mocks.confirm,
  }),
}))

vi.mock('@/components/ui', () => ({
  UiBadge: { template: '<span class="ui-badge"><slot /></span>' },
  UiButton: {
    props: ['disabled', 'type', 'variant', 'size'],
    template: '<button :type="type || \'button\'" :disabled="disabled"><slot /></button>',
  },
  UiInput: {
    props: ['modelValue', 'disabled', 'type', 'placeholder', 'min', 'max', 'step'],
    emits: ['update:modelValue'],
    template:
      '<input :type="type || \'text\'" :value="modelValue" :disabled="disabled" :placeholder="placeholder" @input="$emit(\'update:modelValue\', $event.target.value)" />',
  },
  UiLabel: { template: '<label><slot /></label>' },
  UiSelectField: {
    props: ['modelValue', 'options', 'disabled', 'placeholder'],
    emits: ['update:modelValue'],
    template:
      '<select :value="modelValue" :disabled="disabled" @change="$emit(\'update:modelValue\', $event.target.value)"><option v-for="opt in options" :key="opt.value" :value="opt.value">{{ opt.label }}</option></select>',
  },
  UiSwitch: {
    props: ['modelValue', 'disabled'],
    emits: ['update:modelValue'],
    template:
      '<button type="button" role="switch" :aria-checked="Boolean(modelValue)" :disabled="disabled" @click="$emit(\'update:modelValue\', !modelValue)" />',
  },
  UiTextarea: {
    props: ['modelValue', 'disabled', 'rows', 'placeholder'],
    emits: ['update:modelValue'],
    template:
      '<textarea :rows="rows" :value="modelValue" :disabled="disabled" :placeholder="placeholder" @input="$emit(\'update:modelValue\', $event.target.value)" />',
  },
}))

import {
  contextMemoryApi,
  extractMarkdownMemoryAnchors,
  parseCompanionStructuredMarkdown,
  serializeCompanionStructuredMarkdown,
} from '@/lib/contextMemoryApi'
import ModeContextStrategySection from '@/components/modeEditor/ModeContextStrategySection.vue'
import { MEMORY_CONTEXT_MESSAGES } from '@/settings/memoryContext/memoryContextI18n'
import MemoryContextSection from './MemoryContextSection.vue'

const i18nPlugin = createI18n({
  legacy: false,
  locale: 'zh-CN',
  fallbackLocale: 'en',
  messages: { en, 'zh-CN': en },
})

function jsonResponse(body: unknown, status = 200, headers?: Record<string, string>): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'content-type': 'application/json',
      ...(headers ?? {}),
    },
  })
}

const basePolicy = {
  strategy: 'managed',
  recall_fidelity: 'balanced',
  compaction_trigger_ratio: 0.75,
  recent_message_limit: 128,
  allow_cross_session_recall: true,
  micro_compact_stale_tool_outputs: true,
  offload_large_tool_payload_bytes: 16384,
  evolution_mode: 'auto_evolve',
  candidate_only: false,
  core_markdown_max_tokens: 1500,
  project_markdown_projection: true,
  project_memory_path: '.tinadec/memory/project-core.md',
  companion_can_write_project_memory: false,
  content_hash: 'hash-policy-1',
}

describe('Phase 11 Context & Memory Frontend', () => {
  const originalFetch = globalThis.fetch

  beforeEach(() => {
    vi.clearAllMocks()
    setHostAccessStatus({ state: 'ready', managed: true })
    pendingSettingsSection.value = null
    mocks.listProjects.mockResolvedValue([
      { id: 'proj-1', name: 'TinadecOffice', path: 'C:/git/agent/TinadecOffice', created_at: '2026-11-22T00:00:00Z' },
    ])
    mocks.listCompanions.mockResolvedValue([
      { id: 'comp-1', name: 'Nova', role: 'autonomous', status: 'active', revision: 1 },
    ])
  })

  afterEach(() => {
    globalThis.fetch = originalFetch
  })

  it('guards backend calls with useHostAccess().canAccessBackend and round-trips companion structured Markdown with anchors', async () => {
    const fetchMock = vi.fn()
    globalThis.fetch = fetchMock as unknown as typeof fetch

    setHostAccessStatus({ state: 'preview', managed: true })
    await expect(contextMemoryApi.getPolicy()).rejects.toMatchObject({ status: 503 })
    expect(fetchMock).not.toHaveBeenCalled()

    setHostAccessStatus({ state: 'ready', managed: true })
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        fidelity: 'balanced',
        summary: 'Discussed migration strategy.',
        verbatim_snippets: [
          {
            session_id: 'sess-99',
            message_seq: 14,
            role: 'assistant',
            created_at: '2026-11-22T05:00:00Z',
            excerpt: 'Use immutable version records.',
          },
        ],
      }),
    )

    const recall = await contextMemoryApi.recallSessionHistory('sess-99', {
      query: 'migration',
      fidelity: 'balanced',
    })
    expect(recall.summary).toContain('migration strategy')
    expect(recall.verbatim_snippets[0]?.message_seq).toBe(14)

    const sampleMd = [
      '## Persona & Bond',
      '- Concise technical partner <!-- mem:m-1 v2 src:sess-01#42 -->',
      '',
      '## Episodic Highlights',
      '- Shipped Phase 11 design',
      '',
      '## Operational Focus',
      '- Keep tests focused',
      '',
      '## Cross-Project Notes',
      '- Prefer Bun for frontend scripts',
    ].join('\n')

    const parsed = parseCompanionStructuredMarkdown(sampleMd)
    expect(parsed.sections.persona_bond).toContain('mem:m-1')
    expect(parsed.sections.cross_project_notes).toContain('Prefer Bun')

    const roundTripped = serializeCompanionStructuredMarkdown(parsed)
    const anchors = extractMarkdownMemoryAnchors(roundTripped)
    expect(anchors).toHaveLength(1)
    expect(anchors[0]).toMatchObject({
      id: 'm-1',
      version: 2,
      sourceSessionId: 'sess-01',
      sourceMessageSeq: 42,
    })
  })

  it('drives the 4-tab MemoryContextSection workbench (Markdown sync, provenance + 1-click revert, token budget meter, companion sections, policy & TOML jump)', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      const method = (init?.method ?? 'GET').toUpperCase()

      if (url.includes('/api/v1/context-memory/policy')) {
        if (method === 'PUT') {
          const body = JSON.parse(String(init?.body ?? '{}'))
          return jsonResponse({ ...basePolicy, ...body, content_hash: 'hash-policy-2' })
        }
        return jsonResponse(basePolicy)
      }

      if (url.includes('/api/v1/memories/documents/principal')) {
        if (method === 'PUT') {
          const body = JSON.parse(String(init?.body ?? '{}'))
          return jsonResponse({
            scope: 'principal',
            file_path: 'memory/user-profile.md',
            markdown: body.markdown,
            estimated_tokens: 180,
            core_markdown_max_tokens: 1500,
            active_hot_count: 2,
            cold_archive_count: 0,
            content_hash: 'hash-p2',
          })
        }
        return jsonResponse({
          scope: 'principal',
          file_path: 'memory/user-profile.md',
          markdown: '- Prefer concise tests <!-- mem:item-1 v2 src:sess-abc123#42 -->\n',
          estimated_tokens: 120,
          core_markdown_max_tokens: 1500,
          active_hot_count: 1,
          cold_archive_count: 0,
          content_hash: 'hash-p1',
        })
      }

      if (url.includes('/api/v1/memories/documents/project')) {
        return jsonResponse({
          scope: 'project',
          file_path: '.tinadec/memory/project-core.md',
          markdown: '## Architecture\n- SQLite immutable ledger <!-- mem:proj-item-1 v1 src:sess-p1#8 -->\n',
          estimated_tokens: 640,
          core_markdown_max_tokens: 1500,
          active_hot_count: 6,
          cold_archive_count: 19,
          content_hash: 'hash-proj-1',
        })
      }

      if (url.includes('/api/v1/memories/documents/companion')) {
        return jsonResponse({
          scope: 'companion',
          file_path: 'companions/comp-1/memory.md',
          markdown:
            '## Persona & Bond\n\n- Calm reviewer\n\n## Episodic Highlights\n\n- Nightly triage\n\n## Operational Focus\n\n- Watch CI\n\n## Cross-Project Notes\n\n- Shared monorepo conventions\n',
          estimated_tokens: 210,
          core_markdown_max_tokens: 1500,
          active_hot_count: 4,
          cold_archive_count: 2,
          content_hash: 'hash-comp-1',
        })
      }

      if (url.includes('/api/v1/memories/items/item-1/revert') && method === 'POST') {
        return jsonResponse({
          id: 'item-1',
          scope: 'principal',
          kind: 'preference',
          action: 'add',
          status: 'active',
          current_version: 1,
          content: 'Original test preference',
          in_hot_markdown: true,
        })
      }

      if (url.includes('/api/v1/memories/items') && method === 'POST') {
        const body = JSON.parse(String(init?.body ?? '{}'))
        return jsonResponse({
          id: 'item-new',
          scope: body.scope,
          kind: body.kind,
          action: 'add',
          status: 'active',
          current_version: 1,
          content: body.content,
          in_hot_markdown: true,
        }, 201)
      }

      if (url.includes('/api/v1/memories/items')) {
        return jsonResponse({
          items: [
            {
              id: 'item-1',
              scope: 'principal',
              kind: 'preference',
              action: 'supersede',
              status: 'active',
              current_version: 2,
              content: 'Prefer concise high-signal Vitest suites',
              previous_content: 'Write exhaustive unit tests everywhere',
              source_session_id: 'sess-abc123',
              source_message_seq: 42,
              in_hot_markdown: true,
              created_at: '2026-11-22T05:00:00Z',
              updated_at: '2026-11-22T06:00:00Z',
            },
          ],
        })
      }

      return jsonResponse({})
    })
    globalThis.fetch = fetchMock as unknown as typeof fetch

    const wrapper = mount(MemoryContextSection, {
      global: { plugins: [i18nPlugin] },
    })
    await flushPromises()

    // Tab 1: Principal — provenance, mutation badge, 1-click revert, manual add, direct Markdown save
    expect(wrapper.get('[data-testid="memory-item-mutation-badge"]').text()).toContain('supersede')
    expect(wrapper.get('[data-testid="memory-item-provenance-link"]').text()).toContain('#42')

    await wrapper.get('[data-testid="memory-item-revert-btn"]').trigger('click')
    await flushPromises()
    expect(fetchMock.mock.calls.some(([u, i]) => String(u).includes('/api/v1/memories/items/item-1/revert') && i?.method === 'POST')).toBe(true)

    await wrapper.get('[data-testid="memory-ledger-open-add-btn"]').trigger('click')
    await wrapper.get('[data-testid="memory-add-content-input"]').setValue('Always use semantic CSS tokens')
    await wrapper.get('[data-testid="memory-ledger-add-form"]').trigger('submit')
    await flushPromises()
    expect(fetchMock.mock.calls.some(([u, i]) => String(u).endsWith('/api/v1/memories/items') && i?.method === 'POST')).toBe(true)

    await wrapper.get('[data-testid="principal-markdown-textarea"]').setValue('# Updated Profile\n- Semantic CSS tokens\n')
    await wrapper.get('[data-testid="principal-save-markdown-btn"]').trigger('click')
    await flushPromises()
    expect(fetchMock.mock.calls.some(([u, i]) => String(u).includes('/api/v1/memories/documents/principal') && i?.method === 'PUT')).toBe(true)

    // Tab 2: Project — Core Markdown Token Budget Meter (640 / 1500, 6 hot vs 19 cold archive)
    await wrapper.get('[data-testid="memory-tab-project"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="project-token-budget-fraction"]').text()).toContain('640 / 1500')
    expect(wrapper.get('[data-testid="project-hot-items-count"]').text()).toContain('6')
    expect(wrapper.get('[data-testid="project-cold-archive-count"]').text()).toContain('19')

    // Tab 3: Companion — 4 structured sections + companion_can_write_project_memory toggle
    await wrapper.get('[data-testid="memory-tab-companion"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="companion-section-persona_bond"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="companion-section-episodic_highlights"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="companion-section-operational_focus"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="companion-section-cross_project_notes"]').exists()).toBe(true)

    await wrapper.get('[data-testid="companion-write-project-memory-switch"]').trigger('click')
    await flushPromises()
    expect(
      fetchMock.mock.calls.some(
        ([u, i]) =>
          String(u).includes('/api/v1/context-memory/policy') &&
          i?.method === 'PUT' &&
          String(i.body).includes('"companion_can_write_project_memory":true'),
      ),
    ).toBe(true)

    // Tab 4: Policy & TOML — Always-On informative pills (no switches inside), 3-tier update, TOML jump
    await wrapper.get('[data-testid="memory-tab-policy"]').trigger('click')
    await flushPromises()
    const alwaysOn = wrapper.get('[data-testid="always-on-primitives"]')
    expect(alwaysOn.text()).toContain('KV-Cache')
    expect(alwaysOn.findAll('[role="switch"]')).toHaveLength(0)

    await wrapper.get('[data-testid="context-strategy-tier-deep_curator"]').trigger('click')
    await wrapper.get('[data-testid="save-context-policy-btn"]').trigger('click')
    await flushPromises()
    expect(
      fetchMock.mock.calls.some(
        ([u, i]) =>
          String(u).includes('/api/v1/context-memory/policy') &&
          i?.method === 'PUT' &&
          String(i.body).includes('"strategy":"deep_curator"'),
      ),
    ).toBe(true)

    await wrapper.get('[data-testid="jump-runtime-toml-btn"]').trigger('click')
    expect(pendingSettingsSection.value).toBe('agentCenter')
    expect(readCenterUiState().agentCenter.tab).toBe('advanced')
    expect(wrapper.emitted('open-runtime-toml')).toHaveLength(1)

    wrapper.unmount()
  })

  it('supports compact & inheritable ModeContextStrategySection drop-in with collapsible advanced drawer', async () => {
    const wrapper = mount(ModeContextStrategySection, {
      props: {
        strategy: null,
        advanced: null,
        compact: true,
        allowInherit: true,
      },
      global: { plugins: [i18nPlugin] },
    })

    expect(wrapper.get('[data-testid="context-strategy-state-pill"]').text()).toBeTruthy()
    expect(wrapper.find('[data-testid="context-advanced-panel"]').exists()).toBe(false)

    await wrapper.get('[data-testid="context-strategy-tier-direct"]').trigger('click')
    expect(wrapper.emitted('update:strategy')?.[0]).toEqual(['direct'])

    await wrapper.setProps({ strategy: 'direct' })
    await wrapper.get('[data-testid="context-advanced-toggle"]').trigger('click')
    expect(wrapper.find('[data-testid="context-advanced-panel"]').exists()).toBe(true)

    await wrapper.get('[data-testid="adv-compaction-ratio"]').setValue('0.85')
    expect(wrapper.emitted('update:advanced')?.[0]).toEqual([{ compaction_trigger_ratio: 0.85 }])

    await wrapper.get('[data-testid="context-strategy-reset-btn"]').trigger('click')
    expect(wrapper.emitted('reset')).toHaveLength(1)
    expect(wrapper.emitted('update:strategy')?.at(-1)).toEqual([null])

    // 5-locale dictionary key & placeholder parity check
    const enKeys = Object.keys(MEMORY_CONTEXT_MESSAGES.en).sort()
    const placeholders = (s: string) => [...new Set(s.match(/\{[A-Za-z_][A-Za-z0-9_]*\}/g) ?? [])].sort().join(' ')
    for (const locale of ['zh-CN', 'zh-TW', 'en', 'ja', 'ru'] as const) {
      expect(Object.keys(MEMORY_CONTEXT_MESSAGES[locale]).sort()).toEqual(enKeys)
      for (const key of enKeys) {
        expect(placeholders(MEMORY_CONTEXT_MESSAGES[locale][key]!)).toBe(placeholders(MEMORY_CONTEXT_MESSAGES.en[key]!))
        expect(MEMORY_CONTEXT_MESSAGES[locale][key]).not.toContain('|')
      }
    }

    wrapper.unmount()
  })
})
