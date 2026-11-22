// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import type { AgentModeTopologyDto, SpaceOptionsDto } from '@/api'

const h0 = vi.hoisted(() => ({
  modes: [] as AgentModeTopologyDto[],
  listAgentModeTopologies: vi.fn(async () => h0.modes),
}))

vi.mock('@/api', () => ({
  api: {
    listAgentModeTopologies: h0.listAgentModeTopologies,
  },
}))

const { default: ComposerCommandPanel } = await import('./ComposerCommandPanel.vue')
const { defaultSpaceOptions } = await import('@/lib/composerSettings')

const host = {
  canStop: () => false,
  draft: () => '',
  setDraft: () => {},
  send: () => {},
  stopRun: () => {},
  newSession: () => {},
  navigate: () => {},
  routeName: () => null,
}

const teamDefaults: SpaceOptionsDto = {
  plan_first: false,
  spec_enabled: false,
  multi_agent: true,
  workflow_mode_version_id: null,
  bulletin_board: true,
  worktree: false,
}

function makeMode(
  id: string,
  slug: string,
  displayName: string,
  versionId: string,
  defaultOptions?: SpaceOptionsDto | null,
): AgentModeTopologyDto {
  return {
    id,
    slug,
    display_name: displayName,
    status: 'published',
    latest_published_mode_version_id: versionId,
    nodes: [],
    edges: [{ id: 'e1', source: 'n1', target: 'n2' }],
    default_options: defaultOptions,
  }
}

function mountPanel(props: Partial<{
  spatial: boolean
  initialPage: 'root' | 'mode'
  modeVersionId: string | null
  spaceOptions: SpaceOptionsDto | null
  settingsSaving: boolean
}> = {}) {
  const anchor = document.createElement('div')
  document.body.appendChild(anchor)
  const wrapper = mount(defineComponent({
    emits: ['select-mode', 'update:modeVersionId', 'update:spaceOptions'],
    setup(_, { emit }) {
      return () => h(ComposerCommandPanel, {
        open: true,
        anchor,
        canAttach: true,
        permission: 'default',
        commandHost: host,
        spatial: props.spatial ?? false,
        initialPage: props.initialPage ?? 'root',
        modeVersionId: props.modeVersionId ?? null,
        spaceOptions: props.spaceOptions ?? null,
        settingsSaving: props.settingsSaving ?? false,
        'onSelect-mode': (payload: unknown) => emit('select-mode', payload),
        'onUpdate:modeVersionId': (value: unknown) => emit('update:modeVersionId', value),
        'onUpdate:spaceOptions': (value: unknown) => emit('update:spaceOptions', value),
      })
    },
  }), {
    attachTo: document.body,
    global: {
      plugins: [createI18n({ legacy: false, locale: 'en', messages: { en } })],
    },
  })
  return wrapper
}

const click = async (testId: string) => {
  const el = document.querySelector<HTMLElement>(`[data-testid="${testId}"]`)
  expect(el, testId).not.toBeNull()
  el!.click()
  await flushPromises()
}

afterEach(() => {
  vi.clearAllMocks()
  h0.modes = []
  document.body.innerHTML = ''
})

describe('ComposerCommandPanel unified mode preset & ephemeral overrides', () => {
  it('shows mode and all 5 run switches in both flat and spatial surfaces, keeping workflow spatial-only', async () => {
    h0.modes = [makeMode('m-solo', 'solo', 'Solo', 'mv-solo', null)]
    const flat = mountPanel({ spatial: false, modeVersionId: 'mv-solo' })
    await flushPromises()
    expect(document.querySelector('[data-testid="command-panel-mode"]')).not.toBeNull()
    expect(document.querySelector('[data-testid="command-panel-workflow"]')).toBeNull()
    expect(document.querySelectorAll('[role="switch"]')).toHaveLength(5)
    flat.unmount()
    document.body.innerHTML = ''

    const spatial = mountPanel({ spatial: true, modeVersionId: 'mv-solo' })
    await flushPromises()
    expect(document.querySelector('[data-testid="command-panel-mode"]')).not.toBeNull()
    expect(document.querySelector('[data-testid="command-panel-workflow"]')).not.toBeNull()
    expect(document.querySelectorAll('[role="switch"]')).toHaveLength(5)
    spatial.unmount()
  })

  it('displays active mode default_options when spaceOptions is null and preserves preset flags on toggle', async () => {
    h0.modes = [makeMode('m-team', 'free_director', 'Team', 'mv-team', teamDefaults)]
    const wrapper = mountPanel({ spatial: false, modeVersionId: 'mv-team', spaceOptions: null })
    await flushPromises()

    expect(document.querySelector('[data-testid="command-panel-agents"]')?.getAttribute('aria-checked')).toBe('true')
    expect(document.querySelector('[data-testid="command-panel-bulletin"]')?.getAttribute('aria-checked')).toBe('true')
    expect(document.querySelector('[data-testid="command-panel-worktree"]')?.getAttribute('aria-checked')).toBe('false')
    expect(document.querySelector('[data-testid="command-panel-overrides"]')).toBeNull()

    await click('command-panel-worktree')
    expect(wrapper.emitted('update:spaceOptions')?.[0]?.[0]).toEqual({ ...teamDefaults, worktree: true })
    wrapper.unmount()
  })

  it('shows temporary override badge + 1-click reset when spaceOptions differs from mode default_options', async () => {
    h0.modes = [makeMode('m-team', 'free_director', 'Team', 'mv-team', teamDefaults)]
    const wrapper = mountPanel({
      spatial: false,
      modeVersionId: 'mv-team',
      spaceOptions: { ...teamDefaults, bulletin_board: false, plan_first: true },
    })
    await flushPromises()

    expect(document.querySelector('[data-testid="command-panel-overrides"]')?.textContent).toContain('Temporarily adjusted')
    await click('command-panel-reset-options')
    expect(wrapper.emitted('update:spaceOptions')?.[0]?.[0]).toEqual(teamDefaults)
    wrapper.unmount()
  })

  it('emits select-mode with mode default_options or defaultSpaceOptions() fallback when switching modes', async () => {
    h0.modes = [
      makeMode('m-solo', 'solo', 'Solo', 'mv-solo', undefined),
      makeMode('m-team', 'free_director', 'Team', 'mv-team', teamDefaults),
    ]
    const wrapper = mountPanel({ spatial: true, initialPage: 'mode', modeVersionId: 'mv-solo' })
    await flushPromises()

    await click('command-panel-mv-team')
    expect(wrapper.emitted('select-mode')?.at(-1)?.[0]).toEqual({
      mode_version_id: 'mv-team',
      space_options: teamDefaults,
    })

    await click('command-panel-mv-solo')
    expect(wrapper.emitted('select-mode')?.at(-1)?.[0]).toEqual({
      mode_version_id: 'mv-solo',
      space_options: defaultSpaceOptions(),
    })
    wrapper.unmount()
  })
})
