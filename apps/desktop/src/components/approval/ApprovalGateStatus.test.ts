// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createI18n } from 'vue-i18n'
import ApprovalGateStatus from './ApprovalGateStatus.vue'
import zh from '@/locales/zh-CN'
import en from '@/locales/en'
import type { ApprovalGateDto, ApprovalGatesDto } from '@/api'

const mocks = vi.hoisted(() => ({ getApprovalGates: vi.fn() }))
vi.mock('@/api', () => ({ api: { getApprovalGates: mocks.getApprovalGates } }))

const gate = (index: number, kind: string, status: string, extra: Partial<ApprovalGateDto> = {}): ApprovalGateDto => ({
  gate_index: index, gate_kind: kind, status, decider_agent: null, reason: null, evidence: null,
  created_at: '2026-09-29T08:00:00Z', decided_at: null, ...extra,
})
const gates = (status: string, items: ApprovalGateDto[]): ApprovalGatesDto => ({ approval_id: 'a-1', run_id: 'r-1', status, gates: items })

function mountStatus(approvalId = 'a-1') {
  const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh, en } })
  return mount(ApprovalGateStatus, { props: { approvalId }, global: { plugins: [i18n] } })
}

describe('ApprovalGateStatus', () => {
  beforeEach(() => { vi.useFakeTimers(); mocks.getApprovalGates.mockReset() })
  afterEach(() => { vi.useRealTimers() })

  it('renders nothing for an approval that was never delegated', async () => {
    mocks.getApprovalGates.mockResolvedValue(null)
    const wrapper = mountStatus()
    await flushPromises()
    expect(wrapper.find('.approval-gates').exists()).toBe(false)
    expect(mocks.getApprovalGates).toHaveBeenCalledWith('a-1')
  })

  it('shows each gate, who decided and why, and polls only while a gate is still deciding', async () => {
    mocks.getApprovalGates
      .mockResolvedValueOnce(gates('evaluating', [
        gate(0, 'reviewer_agent', 'approved', { decider_agent: 'governance_reviewer', reason: '写入 docs/a.md 正是任务要的。' }),
        gate(1, 'conversation_identity', 'pending'),
      ]))
      .mockResolvedValueOnce(gates('approved', [
        gate(0, 'reviewer_agent', 'approved', { decider_agent: 'governance_reviewer' }),
        gate(1, 'conversation_identity', 'approved', { decider_agent: 'meeting' }),
      ]))
    const wrapper = mountStatus()
    await flushPromises()
    const rows = wrapper.findAll('li')
    expect(rows).toHaveLength(2)
    expect(rows[0]!.text()).toContain('审查员')
    expect(rows[0]!.text()).toContain('governance_reviewer')
    expect(rows[0]!.text()).toContain('已批准')
    expect(rows[0]!.text()).toContain('写入 docs/a.md 正是任务要的。')
    expect(rows[1]!.text()).toContain('对话身份')
    expect(rows[1]!.text()).toContain('待审')

    await vi.advanceTimersByTimeAsync(2000)
    await flushPromises()
    expect(mocks.getApprovalGates).toHaveBeenCalledTimes(2)
    expect(wrapper.findAll('li')[1]!.text()).toContain('已批准')
    // Settled: no further polling.
    await vi.advanceTimersByTimeAsync(10000)
    expect(mocks.getApprovalGates).toHaveBeenCalledTimes(2)
  })

  it('says when a gate handed the decision back', async () => {
    mocks.getApprovalGates.mockResolvedValue(gates('escalated', [
      gate(0, 'reviewer_agent', 'escalated', { reason: '看不出该不该写。' }),
    ]))
    const wrapper = mountStatus()
    await flushPromises()
    expect(wrapper.text()).toContain('已交还给你决定')
    expect(wrapper.text()).toContain('看不出该不该写。')
    await vi.advanceTimersByTimeAsync(10000)
    expect(mocks.getApprovalGates).toHaveBeenCalledTimes(1)
  })

  it('stops polling when unmounted', async () => {
    mocks.getApprovalGates.mockResolvedValue(gates('evaluating', [gate(0, 'reviewer_agent', 'evaluating')]))
    const wrapper = mountStatus()
    await flushPromises()
    wrapper.unmount()
    await vi.advanceTimersByTimeAsync(10000)
    expect(mocks.getApprovalGates).toHaveBeenCalledTimes(1)
  })
})
