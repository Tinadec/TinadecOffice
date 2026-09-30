// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createI18n } from 'vue-i18n'
import EnvironmentList from './EnvironmentList.vue'
import zh from '@/locales/zh-CN'
import en from '@/locales/en'
import type { EnvironmentDto } from '@/api'

const mocks = vi.hoisted(() => ({ listEnvironments: vi.fn(), registerEnvironment: vi.fn(), updateEnvironment: vi.fn() }))
vi.mock('@/api', () => ({ api: mocks }))

const environment = (key: string, extra: Partial<EnvironmentDto> = {}): EnvironmentDto => ({
  id: `id-${key}`, key, kind: 'test', display_name: key.toUpperCase(), description: null, connection: {}, capacity: 2, free_slots: 1,
  status: 'available', updated_at: '2026-09-30T08:00:00Z',
  holders: [{ slot: 1, lease_id: 'l-1', session_id: 's-1', run_id: 'run-aaaaaaaa-1', task_id: null, reason: 'e2e' }], ...extra,
})

function mountList(sessionId: string | null = 's-1') {
  const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh, en } })
  return mount(EnvironmentList, { props: { sessionId }, global: { plugins: [i18n] } })
}

describe('EnvironmentList', () => {
  beforeEach(() => Object.values(mocks).forEach((mock) => mock.mockReset()))

  it('shows each environment with its free slots and who holds them, marking this session', async () => {
    mocks.listEnvironments.mockResolvedValue([environment('staging'), environment('gpu', { kind: 'remote', status: 'disabled', holders: [], free_slots: 0 })])
    const wrapper = mountList()
    await flushPromises()
    const [staging, gpu] = wrapper.findAll('.environment-item')
    expect(staging!.text()).toContain('测试')
    expect(staging!.text()).toContain('1/2 空闲')
    expect(staging!.text()).toContain('本会话 · #1 run run-aaaa')
    expect(gpu!.text()).toContain('已停用')
    expect(gpu!.text()).toContain('启用')
  })

  it('registers through Core and refuses a connection that is not a JSON object before sending', async () => {
    mocks.listEnvironments.mockResolvedValue([])
    const wrapper = mountList()
    await flushPromises()
    expect(wrapper.text()).toContain('还没有登记任何环境')
    await wrapper.get('[data-testid="environments-register"]').trigger('click')
    await wrapper.get('[data-testid="environment-key"]').setValue('cloud-dev')
    await wrapper.get('[data-testid="environment-name"]').setValue('Cloud dev')
    await wrapper.get('[data-testid="environment-kind"]').setValue('cloud')
    await wrapper.get('[data-testid="environment-connection"]').setValue('[1]')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(mocks.registerEnvironment).not.toHaveBeenCalled()
    expect(wrapper.get('[role="alert"]').text()).toContain('JSON 对象')

    mocks.registerEnvironment.mockResolvedValue(environment('cloud-dev', { kind: 'cloud', holders: [], free_slots: 2 }))
    await wrapper.get('[data-testid="environment-connection"]').setValue('{"region":"eu-1","secret_ref":"k"}')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(mocks.registerEnvironment).toHaveBeenCalledWith({
      key: 'cloud-dev', kind: 'cloud', display_name: 'Cloud dev', capacity: 1, connection: { region: 'eu-1', secret_ref: 'k' },
    })
    expect(wrapper.find('[data-environment-key="cloud-dev"]').exists()).toBe(true)
  })

  it('switches an environment off in Core and shows what Core returned', async () => {
    mocks.listEnvironments.mockResolvedValue([environment('staging')])
    mocks.updateEnvironment.mockResolvedValue(environment('staging', { status: 'disabled', free_slots: 0 }))
    const wrapper = mountList()
    await flushPromises()
    await wrapper.get('[data-testid="environment-toggle-staging"]').trigger('click')
    await flushPromises()
    expect(mocks.updateEnvironment).toHaveBeenCalledWith('id-staging', { status: 'disabled' })
    expect(wrapper.get('[data-environment-key="staging"]').text()).toContain('已停用')
  })
})
