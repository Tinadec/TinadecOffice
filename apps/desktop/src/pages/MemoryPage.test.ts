// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import MemoryPage from './MemoryPage.vue'
import type { MemoryCandidateDto, MemoryItemDto } from '@/api'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}))

vi.mock('@/api', () => ({
  api: {
    listMemoryCandidates: vi.fn(),
    promoteMemoryCandidate: vi.fn(),
    rejectMemoryCandidate: vi.fn(),
    listMemoryItems: vi.fn(),
    revokeMemoryItem: vi.fn(),
  },
}))

import { api } from '@/api'
const apiMock = vi.mocked(api, true)

function candidate(overrides: Partial<MemoryCandidateDto> = {}): MemoryCandidateDto {
  return {
    id: 'cand-1',
    source_run_id: 'run-1',
    generated_by_instance_id: 'inst-1',
    scope: 'workspace',
    kind: 'fact',
    status: 'proposed',
    confidence: 0.82,
    content: 'This repo restores only under the pinned SDK band.',
    evidence: 'run log line 42',
    applicability: 'the global.json still pins 10.0',
    expiry_condition: 'the pin moves to another band',
    decision_reason: null,
    promoted_memory_item_id: null,
    created_at: '2026-09-22T00:00:00Z',
    updated_at: '2026-09-22T00:00:00Z',
    ...overrides,
  }
}

function item(overrides: Partial<MemoryItemDto> = {}): MemoryItemDto {
  return {
    id: 'item-1',
    scope: 'workspace',
    kind: 'preference',
    status: 'active',
    version: 1,
    content: 'Answer in Chinese.',
    applicability: null,
    expiry_condition: null,
    created_at: '2026-09-20T00:00:00Z',
    updated_at: '2026-09-21T00:00:00Z',
    revoked_at: null,
    ...overrides,
  }
}

function emptyShelves(): void {
  apiMock.listMemoryCandidates.mockResolvedValue([])
  apiMock.listMemoryItems.mockResolvedValue([])
}

afterEach(() => {
  document.body.innerHTML = ''
  // reset, not clear: a resolved value left over from the previous case would answer this
  // one's mount load before this case ever gets to state what the shelf holds.
  vi.resetAllMocks()
})

async function mounted() {
  const wrapper = mount(MemoryPage)
  await flushPromises()
  return wrapper
}

describe('MemoryPage', () => {
  it('opens on the proposed queue and the active shelf, both one page wide', async () => {
    emptyShelves()
    const wrapper = await mounted()

    expect(apiMock.listMemoryCandidates).toHaveBeenCalledWith({ status: 'proposed', scope: undefined, limit: 100 })
    expect(apiMock.listMemoryItems).toHaveBeenCalledWith({ status: 'active', scope: undefined, limit: 100 })
    expect(wrapper.find('[data-testid="memory-queue-empty"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="memory-shelf-empty"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('shows the grounds a candidate was proposed on, because the sentence alone is not reviewable', async () => {
    emptyShelves()
    apiMock.listMemoryCandidates.mockResolvedValue([candidate()])
    const wrapper = await mounted()

    const card = wrapper.find('[data-testid="memory-candidate"]')
    expect(card.text()).toContain('This repo restores only under the pinned SDK band.')
    expect(card.text()).toContain('run log line 42')
    expect(card.text()).toContain('the global.json still pins 10.0')
    expect(card.text()).toContain('the pin moves to another band')
    wrapper.unmount()
  })

  it('omits the grounds block when a candidate carries none, rather than rendering empty rows', async () => {
    emptyShelves()
    apiMock.listMemoryCandidates.mockResolvedValue([
      candidate({ evidence: null, applicability: null, expiry_condition: null }),
    ])
    const wrapper = await mounted()

    const card = wrapper.find('[data-testid="memory-candidate"]')
    expect(card.find('.memory-card__grounds').exists()).toBe(false)
    expect(card.text()).toContain('confidence 82%')
    wrapper.unmount()
  })

  it('renders a confidence that is not a number as no score instead of NaN%', async () => {
    emptyShelves()
    apiMock.listMemoryCandidates.mockResolvedValue([candidate({ confidence: Number.NaN })])
    const wrapper = await mounted()

    const card = wrapper.find('[data-testid="memory-candidate"]')
    expect(card.text()).toContain('no score')
    expect(card.text()).not.toContain('NaN')
    wrapper.unmount()
  })

  it('promoting hands the candidate to Core and takes it off the proposed shelf', async () => {
    emptyShelves()
    // Once: the shelf answers "nothing proposed" after the decision, which is the fact
    // the row was removed on.
    apiMock.listMemoryCandidates.mockResolvedValueOnce([candidate()])
    apiMock.promoteMemoryCandidate.mockResolvedValue(candidate({ status: 'promoted', promoted_memory_item_id: 'item-9' }))
    const wrapper = await mounted()

    await wrapper.find('[data-testid="memory-promote-cand-1"]').trigger('click')
    await flushPromises()

    expect(apiMock.promoteMemoryCandidate).toHaveBeenCalledWith('cand-1')
    expect(wrapper.find('[data-testid="memory-candidate"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('reports a conflict as already answered and keeps the card, so the reviewer knows it is still open', async () => {
    emptyShelves()
    apiMock.listMemoryCandidates.mockResolvedValue([candidate()])
    apiMock.rejectMemoryCandidate.mockRejectedValue(
      Object.assign(new Error('Memory candidate has already been decided.'), { code: 'conflict', status: 409 }),
    )
    const wrapper = await mounted()

    await wrapper.find('[data-testid="memory-reject-cand-1"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="memory-decision-error"]').text()).toBe('Someone already answered this one.')
    expect(wrapper.findAll('[data-testid="memory-candidate"]')).toHaveLength(1)
    wrapper.unmount()
  })

  it('the scope filter narrows both shelves in one request pair', async () => {
    emptyShelves()
    const wrapper = await mounted()
    apiMock.listMemoryCandidates.mockClear()
    apiMock.listMemoryItems.mockClear()

    await wrapper.find('[data-testid="memory-scope"]').setValue('principal')
    await flushPromises()

    expect(apiMock.listMemoryCandidates).toHaveBeenCalledWith({ status: 'proposed', scope: 'principal', limit: 100 })
    expect(apiMock.listMemoryItems).toHaveBeenCalledWith({ status: 'active', scope: 'principal', limit: 100 })
    wrapper.unmount()
  })

  it('forgetting asks Core and reloads both shelves', async () => {
    apiMock.listMemoryItems.mockResolvedValue([item()])
    apiMock.listMemoryCandidates.mockResolvedValue([])
    apiMock.revokeMemoryItem.mockResolvedValue({
      id: 'item-1',
      scope: 'workspace',
      kind: 'preference',
      status: 'revoked',
      version: 1,
      applicability: null,
      expiry_condition: null,
      revoked_at: '2026-09-22T01:00:00Z',
    })
    const wrapper = await mounted()

    await wrapper.find('[data-testid="memory-revoke-item-1"]').trigger('click')
    await flushPromises()

    expect(apiMock.revokeMemoryItem).toHaveBeenCalledWith('item-1')
    // Two loads: the one on mount and the one after the revocation.
    expect(apiMock.listMemoryItems).toHaveBeenCalledTimes(2)
    wrapper.unmount()
  })

  it('a full page says there may be more, and a short one does not', async () => {
    emptyShelves()
    apiMock.listMemoryCandidates.mockResolvedValue(
      Array.from({ length: 100 }, (_, index) => candidate({ id: `cand-${index}` })),
    )
    const full = await mounted()
    expect(full.text()).toContain('Showing the first 100 — there may be more.')
    full.unmount()

    vi.clearAllMocks()
    emptyShelves()
    apiMock.listMemoryCandidates.mockResolvedValue([candidate()])
    const shortPage = await mounted()
    expect(shortPage.text()).not.toContain('there may be more')
    shortPage.unmount()
  })

  it('the history shelf asks for revoked memory and stops offering the forget button', async () => {
    emptyShelves()
    const wrapper = await mounted()
    apiMock.listMemoryItems.mockResolvedValue([item({ status: 'revoked', revoked_at: '2026-09-22T01:00:00Z' })])

    await wrapper.find('[data-testid="memory-toggle-history"]').trigger('click')
    await flushPromises()

    expect(apiMock.listMemoryItems).toHaveBeenLastCalledWith({ status: 'revoked', scope: undefined, limit: 100 })
    expect(wrapper.text()).toContain('Forgotten')
    expect(wrapper.find('[data-testid="memory-revoke-item-1"]').attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })

  it('a shelf that failed to load says so instead of showing an empty queue', async () => {
    apiMock.listMemoryCandidates.mockRejectedValue(
      Object.assign(new Error('Unknown status nonsense.'), { code: 'invalid_request', status: 400 }),
    )
    apiMock.listMemoryItems.mockRejectedValue(new Error('nope'))
    const wrapper = await mounted()

    expect(wrapper.find('[data-testid="memory-load-error"]').text()).toBe('Unknown status nonsense.')
    expect(wrapper.find('[data-testid="memory-queue-empty"]').exists()).toBe(false)
    wrapper.unmount()
  })
})
