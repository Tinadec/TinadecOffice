// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest'
import { composerSettings, pageDepth, parentPage } from './composerCommands'
import {
  applyComposerSettings,
  copyComposerSettings,
  defaultSpaceOptions,
  newComposerSettings,
  normalizeContextAdvanced,
  resolveModeDefaultSpaceOptions,
  spaceOptionsEqual,
} from './composerSettings'
import { emptySpaceOptions, normalizeDefaultOptions } from '@/composables/useAgentModesStore'
import { homeController } from '@/controllers/HomeController'

describe('composerCommands context-strategy promotion', () => {
  it('places context-strategy immediately after permission and before plan as a root submenu', () => {
    const ids = composerSettings.map((item) => item.id)
    const permIdx = ids.indexOf('permission')
    const ctxIdx = ids.indexOf('context-strategy')
    const planIdx = ids.indexOf('plan')

    expect(permIdx).toBeGreaterThanOrEqual(0)
    expect(ctxIdx).toBe(permIdx + 1)
    expect(planIdx).toBe(ctxIdx + 1)

    const ctxEntry = composerSettings[ctxIdx]!
    expect(ctxEntry).toMatchObject({
      id: 'context-strategy',
      label: 'commandPanel.contextStrategy',
      hint: 'commandPanel.contextStrategyHint',
      keywords: 'commandPanel.kw.contextStrategy',
      page: 'context-strategy',
    })
    expect('toggle' in ctxEntry).toBe(false)
    expect(parentPage['context-strategy']).toBeUndefined()
    expect(pageDepth('context-strategy')).toBe(1)
  })
})

describe('composerSettings overlay normalization, diff, and deep copy', () => {
  it('normalizes empty and all-null context_advanced objects to null', () => {
    expect(normalizeContextAdvanced(null)).toBeNull()
    expect(normalizeContextAdvanced(undefined)).toBeNull()
    expect(normalizeContextAdvanced({})).toBeNull()
    expect(
      normalizeContextAdvanced({
        recall_fidelity: null,
        compaction_trigger_ratio: null,
        recent_message_limit: null,
        allow_cross_session_recall: null,
        evolution_mode: null,
        core_markdown_max_tokens: null,
      }),
    ).toBeNull()
  })

  it('aligns mode default_options over runtime_policy.settings in resolveModeDefaultSpaceOptions', () => {
    const fromPolicyOnly = resolveModeDefaultSpaceOptions(
      { plan_first: true },
      {
        kind: 'autonomous_exploration',
        settings: {
          context_strategy: 'deep_curator',
          compaction_trigger_ratio: 0.85,
          recall_policy: 'verbatim_first',
          memory_evolution_mode: 'candidate_only',
          recent_message_limit: 40,
          allow_cross_session_recall: true,
          core_markdown_max_tokens: 2400,
        },
      },
    )

    expect(fromPolicyOnly).toEqual({
      ...defaultSpaceOptions(),
      plan_first: true,
      context_strategy: 'deep_curator',
      context_advanced: {
        recall_fidelity: 'verbatim_first',
        compaction_trigger_ratio: 0.85,
        recent_message_limit: 40,
        allow_cross_session_recall: true,
        evolution_mode: 'candidate_only',
        core_markdown_max_tokens: 2400,
      },
    })

    const withExplicitOverride = resolveModeDefaultSpaceOptions(
      {
        plan_first: false,
        context_strategy: 'direct',
        context_advanced: {
          compaction_trigger_ratio: 0.6,
        },
      },
      {
        kind: 'autonomous_exploration',
        settings: {
          context_strategy: 'deep_curator',
          compaction_trigger_ratio: 0.85,
          recent_message_limit: 40,
        },
      },
    )

    expect(withExplicitOverride.context_strategy).toBe('direct')
    expect(withExplicitOverride.context_advanced).toEqual({
      compaction_trigger_ratio: 0.6,
      recent_message_limit: 40,
    })

    const fromLegacyCanvasFallback = resolveModeDefaultSpaceOptions(
      null,
      null,
      { context_strategy: 'deep_curator', context_advanced: { recent_message_limit: 50 } },
    )
    expect(fromLegacyCanvasFallback.context_strategy).toBe('deep_curator')
    expect(fromLegacyCanvasFallback.context_advanced).toEqual({ recent_message_limit: 50 })
  })

  it('deep-compares context_strategy and all 6 context_advanced fields in spaceOptionsEqual', () => {
    const base = defaultSpaceOptions()
    expect(spaceOptionsEqual(base, { ...base, context_advanced: {} })).toBe(true)
    expect(
      spaceOptionsEqual(base, {
        ...base,
        context_advanced: {
          recall_fidelity: null,
          compaction_trigger_ratio: null,
          recent_message_limit: null,
          allow_cross_session_recall: null,
          evolution_mode: null,
          core_markdown_max_tokens: null,
        },
      }),
    ).toBe(true)

    expect(spaceOptionsEqual(base, { ...base, context_strategy: 'direct' })).toBe(false)

    const fullAdv = {
      recall_fidelity: 'verbatim_first' as const,
      compaction_trigger_ratio: 0.8,
      recent_message_limit: 32,
      allow_cross_session_recall: true,
      evolution_mode: 'candidate_only' as const,
      core_markdown_max_tokens: 2048,
    }
    const withFull = { ...base, context_strategy: 'deep_curator' as const, context_advanced: fullAdv }
    expect(spaceOptionsEqual(withFull, { ...base, context_strategy: 'deep_curator', context_advanced: { ...fullAdv } })).toBe(true)

    expect(spaceOptionsEqual(withFull, { ...withFull, context_advanced: { ...fullAdv, recall_fidelity: 'summary_only' } })).toBe(false)
    expect(spaceOptionsEqual(withFull, { ...withFull, context_advanced: { ...fullAdv, compaction_trigger_ratio: 0.7 } })).toBe(false)
    expect(spaceOptionsEqual(withFull, { ...withFull, context_advanced: { ...fullAdv, recent_message_limit: 16 } })).toBe(false)
    expect(spaceOptionsEqual(withFull, { ...withFull, context_advanced: { ...fullAdv, allow_cross_session_recall: false } })).toBe(false)
    expect(spaceOptionsEqual(withFull, { ...withFull, context_advanced: { ...fullAdv, evolution_mode: 'auto_evolve' } })).toBe(false)
    expect(spaceOptionsEqual(withFull, { ...withFull, context_advanced: { ...fullAdv, core_markdown_max_tokens: 1024 } })).toBe(false)
  })

  it('deep-copies context_advanced in copyComposerSettings and applyComposerSettings', () => {
    const sourceAdv = {
      recall_fidelity: 'balanced' as const,
      compaction_trigger_ratio: 0.75,
      recent_message_limit: 24,
    }
    const initial = applyComposerSettings(newComposerSettings(), {
      mode_version_id: 'mv-1',
      space_options: {
        ...defaultSpaceOptions(),
        context_strategy: 'managed',
        context_advanced: sourceAdv,
      },
    })

    const snapshot = copyComposerSettings(initial)
    sourceAdv.recent_message_limit = 99
    initial.space_options!.context_advanced!.compaction_trigger_ratio = 0.5

    expect(snapshot.space_options?.context_advanced).toEqual({
      recall_fidelity: 'balanced',
      compaction_trigger_ratio: 0.75,
      recent_message_limit: 24,
    })
  })
})

describe('useAgentModesStore defaultOptions normalization & legacy canvasLayout migration', () => {
  it('persists context_strategy and context_advanced on default_options and falls back to legacy canvasLayout', () => {
    expect(emptySpaceOptions()).toEqual({
      plan_first: false,
      spec_enabled: false,
      multi_agent: false,
      bulletin_board: false,
      worktree: false,
      workflow_mode_version_id: null,
      context_strategy: null,
      context_advanced: null,
    })

    // Legacy mode saved in canvas_layout before default_options persistence fix
    const migrated = normalizeDefaultOptions(null, {
      zoom: 1,
      context_strategy: 'deep_curator',
      context_advanced: { compaction_trigger_ratio: 0.85 },
    })
    expect(migrated).toEqual({
      ...emptySpaceOptions(),
      context_strategy: 'deep_curator',
      context_advanced: { compaction_trigger_ratio: 0.85 },
    })

    // Explicit null in default_options after reset is preserved and NOT overwritten by stale canvasLayout
    const explicitlyReset = normalizeDefaultOptions(
      {
        ...emptySpaceOptions(),
        plan_first: true,
        context_strategy: null,
        context_advanced: null,
      },
      {
        context_strategy: 'deep_curator',
        context_advanced: { compaction_trigger_ratio: 0.85 },
      },
    )
    expect(explicitlyReset).toEqual({
      ...emptySpaceOptions(),
      plan_first: true,
      context_strategy: null,
      context_advanced: null,
    })
  })
})

describe('HomeController.selectMode overlay reset & partial defaults handling', () => {
  it('resets active draft session overlay on mode switch even when partial defaults omit plan_first', async () => {
    homeController.startNewConversation(null)
    await homeController.updateComposerSettings({
      mode_version_id: 'mv-1',
      space_options: {
        ...defaultSpaceOptions(),
        worktree: true,
        bulletin_board: true,
        context_strategy: 'direct',
        context_advanced: { recent_message_limit: 12 },
      },
    })
    expect(homeController.composerSettings.value.space_options?.worktree).toBe(true)
    expect(homeController.composerSettings.value.space_options?.context_strategy).toBe('direct')

    // Partial SpaceOptionsDto without plan_first still applies context_strategy and clears previous overlay
    await homeController.selectMode('mv-2', { context_strategy: 'deep_curator' })
    expect(homeController.composerSettings.value).toMatchObject({
      mode_version_id: 'mv-2',
      space_options: {
        ...defaultSpaceOptions(),
        worktree: false,
        bulletin_board: false,
        context_strategy: 'deep_curator',
        context_advanced: null,
      },
    })

    // Mode record with default_options + runtime_policy.settings merges both cleanly
    await homeController.selectMode('mv-3', {
      default_options: { plan_first: true },
      runtime_policy: {
        kind: 'autonomous_exploration',
        settings: { context_strategy: 'managed', compaction_trigger_ratio: 0.8 },
      },
    })
    expect(homeController.composerSettings.value.space_options).toEqual({
      ...defaultSpaceOptions(),
      plan_first: true,
      context_strategy: 'managed',
      context_advanced: { compaction_trigger_ratio: 0.8 },
    })
  })
})
