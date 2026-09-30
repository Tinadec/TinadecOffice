// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createI18n } from 'vue-i18n'
import EvidenceSearch from './EvidenceSearch.vue'
import zh from '@/locales/zh-CN'
import en from '@/locales/en'
import type { EvidenceHitDto, EvidenceRecallDto } from '@/api'

const mocks = vi.hoisted(() => ({ recallEvidence: vi.fn() }))
vi.mock('@/api', () => ({ api: { recallEvidence: mocks.recallEvidence } }))

const hit = (id: string, extra: Partial<EvidenceHitDto> = {}): EvidenceHitDto => ({
  evidence_id: id, kind: 'task_result', title: '写文档', author: 'global_engineering#1', run_id: 'r-1', task_id: 't-1',
  snippet: 'docs/a.md 被主机占用，没能写入。', score: 16.4, matched_by: 'keyword', created_at: '2026-09-29T08:00:00Z', ...extra,
})
const answer = (mode: string, hits: EvidenceHitDto[], note: string | null = null): EvidenceRecallDto => ({ mode, note, hits })

function mountSearch(sessionId: string | null = 's-1') {
  const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh, en } })
  return mount(EvidenceSearch, { props: { sessionId }, global: { plugins: [i18n] } })
}

describe('EvidenceSearch', () => {
  beforeEach(() => mocks.recallEvidence.mockReset())

  it('searches the session archive and shows each hit with its kind, author, snippet and how it matched', async () => {
    mocks.recallEvidence.mockResolvedValue(answer('keyword', [hit('e-1'), hit('e-2', { kind: 'report', title: '冲突', author: 'governance_reviewer', matched_by: 'both' })],
      'No embedding model is configured for semantic recall: keyword recall only.'))
    const wrapper = mountSearch()
    expect(wrapper.text()).toContain('全部原文保存')
    await wrapper.get('[data-testid="evidence-query"]').setValue('docs/a.md')
    await wrapper.get('[data-testid="evidence-kind-report"]').trigger('click')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mocks.recallEvidence).toHaveBeenCalledWith('s-1', 'docs/a.md', { kinds: ['report'], limit: 20 })
    expect(wrapper.get('[data-testid="evidence-mode"]').text()).toContain('关键词')
    expect(wrapper.get('[data-testid="evidence-mode"]').text()).toContain('keyword recall only')
    const items = wrapper.findAll('.evidence-hit')
    expect(items).toHaveLength(2)
    expect(items[0]!.text()).toContain('任务结果')
    expect(items[0]!.text()).toContain('global_engineering#1')
    expect(items[0]!.text()).toContain('docs/a.md 被主机占用')
    expect(items[1]!.text()).toContain('报告')
    expect(items[1]!.text()).toContain('语义与关键词都命中')
  })

  it('does not search for nothing, and says so when nothing matched', async () => {
    const wrapper = mountSearch()
    await wrapper.get('form').trigger('submit')
    expect(mocks.recallEvidence).not.toHaveBeenCalled()

    mocks.recallEvidence.mockResolvedValue(answer('hybrid', []))
    await wrapper.get('[data-testid="evidence-query"]').setValue('nothing like this')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('没有找到匹配的证据')
    expect(wrapper.get('[data-testid="evidence-mode"]').text()).toContain('语义 + 关键词')
  })

  it('drops an answer that arrives after the session changed', async () => {
    let resolve!: (value: EvidenceRecallDto) => void
    mocks.recallEvidence.mockReturnValue(new Promise<EvidenceRecallDto>((done) => { resolve = done }))
    const wrapper = mountSearch('s-1')
    await wrapper.get('[data-testid="evidence-query"]').setValue('docs')
    await wrapper.get('form').trigger('submit')
    await wrapper.setProps({ sessionId: 's-2' })
    resolve(answer('keyword', [hit('late')]))
    await flushPromises()
    expect(wrapper.find('.evidence-hit').exists()).toBe(false)
  })
})
