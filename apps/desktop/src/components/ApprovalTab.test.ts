// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import ApprovalTab from './ApprovalTab.vue'
import type { ApprovalDto, ApprovalRuleDto } from '../api'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (key: string) => key }),
}))

function mountRows(approvals: ApprovalDto[], approvalRules: ApprovalRuleDto[] = []) {
  return mount(ApprovalTab, {
    props: { approvals, approvalRules, shellCommand: '', busy: false, selectedSessionId: 's1' },
  })
}

const park: ApprovalDto = {
  id: 'a1',
  kind: 'permission',
  tool_id: 'shell',
  risk: 'high',
  summary: 'shell → git push origin main',
  arguments: '{"command":"git push origin main"}',
  command: 'git push origin main',
  cwd: 'C:\\repo',
  resource_path: null,
  status: 'pending',
  created_at: '',
}

describe('ApprovalTab decision evidence', () => {
  it('separates pending, allowed and historical decisions into foldable sections', () => {
    const wrapper = mountRows([
      park,
      { ...park, id: 'a-rejected', status: 'rejected', command: 'git push', summary: 'Push changes' },
    ])
    expect(wrapper.find('.approval-section-pending').findAll('.approval-row')).toHaveLength(1)
    expect(wrapper.find('.approval-count-allowed').text()).toBe('0')
    expect(wrapper.find('.approval-history-status').text()).toBe('rejected')
    expect(wrapper.find('.approval-section-collapsed').exists()).toBe(true)
    wrapper.unmount()
  })

  it('renders standing command rules separately from approval history', () => {
    const rule = { id: 'rule-1', kind: 'command_prefix', tool_id: 'shell', pattern: 'npm test', session_id: 's1', created_by_principal_id: 'u1', created_at: '', use_count: 3 } satisfies ApprovalRuleDto
    const wrapper = mountRows([], [rule])
    expect(wrapper.find('.approval-rule-row').text()).toContain('npm test')
    expect(wrapper.find('.approval-rule-revoke').exists()).toBe(true)
    wrapper.find('.approval-rule-revoke').trigger('click')
    expect(wrapper.emitted('revoke-approval-rule')?.[0]).toEqual([rule])
    wrapper.unmount()
  })

  it('offers a distinct action for remembering a command prefix', async () => {
    const wrapper = mountRows([])
    await wrapper.setProps({ shellCommand: 'npm test' })
    await wrapper.get('.approval-rule-request-button').trigger('click')
    expect(wrapper.emitted('create-approval-rule')?.[0]).toEqual([{
      kind: 'command_prefix', tool_id: 'shell', pattern: 'npm test', session_id: 's1',
    }])
  })

  it('names the tool, risk, command, working directory and parameters', () => {
    const wrapper = mountRows([park])
    expect(wrapper.find('.approval-tool').text()).toBe('shell')
    expect(wrapper.find('.approval-risk').text()).toBe('high')
    expect(wrapper.get('.approval-evidence-details').attributes('open')).toBeUndefined()
    wrapper.get('.approval-evidence-details summary').trigger('click')
    expect(wrapper.find('.approval-command').text()).toContain('git push origin main')
    expect(wrapper.find('.approval-cwd').text()).toBe('C:\\repo')
    expect(wrapper.find('.approval-arguments').text()).toContain('approval.parameters')
    wrapper.unmount()
    document.body.innerHTML = ''
  })

  it('surfaces the target path when the call names one', () => {
    const wrapper = mountRows([{ ...park, tool_id: 'write_file', command: null, cwd: null, resource_path: 'C:\\repo\\probe.txt' }])
    expect(wrapper.find('.approval-target').text()).toContain('C:\\repo\\probe.txt')
    wrapper.unmount()
    document.body.innerHTML = ''
  })

  /**
   * Rows minted before Core froze an evidence digest decode to nothing. Rendering
   * empty wrappers would read as "this call has no command", which is a different
   * claim than "we do not know".
   */
  it('omits every evidence block for a legacy row', () => {
    const wrapper = mountRows([{ id: 'a2', kind: 'tool', summary: 'old row', status: 'pending', created_at: '' }])
    expect(wrapper.find('.approval-tool').exists()).toBe(false)
    expect(wrapper.find('.approval-risk').exists()).toBe(false)
    expect(wrapper.find('.approval-command').exists()).toBe(false)
    expect(wrapper.find('.approval-target').exists()).toBe(false)
    expect(wrapper.find('.approval-arguments').exists()).toBe(false)
    expect(wrapper.find('.approval-row').text()).toContain('old row')
    wrapper.unmount()
    document.body.innerHTML = ''
  })

  it('offers the run-scope grant only for a policy park', async () => {
    const wrapper = mountRows([park, { ...park, id: 'a3', kind: 'user_tool' }])
    const always = wrapper.findAll('.always')
    expect(always).toHaveLength(1)
    await always[0]!.trigger('click')
    const [approval, decision, scope] = wrapper.emitted('decide-approval')![0] as [
      ApprovalDto,
      string,
      string | undefined,
    ]
    expect(approval.id).toBe('a1')
    expect(decision).toBe('approved')
    expect(scope).toBe('run')
    wrapper.unmount()
    document.body.innerHTML = ''
  })
})
