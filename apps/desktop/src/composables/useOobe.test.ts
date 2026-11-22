// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { ModelProviderInstanceDto, RuntimeReadinessReceiptDto } from '@/api'
import {
  __resetOobeForTests,
  classifyProbeError,
  closeOobeWizard,
  createDefaultOobeState,
  evaluateReadinessForOobe,
  getNextStep,
  getPrevStep,
  isProviderEffectivelyConfigured,
  normalizeOobeState,
  OOBE_STEPS,
  openOobeWizard,
  queueDeferredHabit,
  replayOobeWizard,
  shouldAutoOpenOobe,
  useOobe,
} from './useOobe'

function makeProvider(overrides: Partial<ModelProviderInstanceDto> = {}): ModelProviderInstanceDto {
  return {
    id: 'prov-1',
    driver: 'openai-compatible',
    display_name: 'OpenAI',
    connection_kind: 'api-key',
    has_api_key: true,
    capabilities: ['chat'],
    enabled: true,
    status: 'ready',
    status_message: '',
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z',
    ...overrides,
  }
}

function makeReceipt(items: RuntimeReadinessReceiptDto['items']): RuntimeReadinessReceiptDto {
  return { status: 'ready', items }
}

describe('useOobe 3-step engine & admission rules', () => {
  beforeEach(() => {
    __resetOobeForTests()
    localStorage.clear()
  })

  it('defines the streamlined 3-step core flow and migrates legacy persisted steps cleanly', () => {
    expect(OOBE_STEPS).toEqual(['welcome', 'provider', 'workspace'])
    expect(getNextStep('welcome')).toBe('provider')
    expect(getNextStep('provider')).toBe('workspace')
    expect(getNextStep('workspace')).toBeNull()
    expect(getPrevStep('workspace')).toBe('provider')

    const migrated = normalizeOobeState({
      version: 1,
      firstRun: { resumeStep: 'habits' },
      tours: {},
    })
    expect(migrated.firstRun.resumeStep).toBe('workspace')
  })

  it('ignores keyless DevSeed placeholders while recognizing CLI / ACP / local runtimes as configured', () => {
    const devSeed = makeProvider({
      id: 'dev-seed-default',
      display_name: 'DevSeed OpenAI',
      connection_kind: 'api-key',
      has_api_key: false,
    })
    expect(isProviderEffectivelyConfigured(devSeed)).toBe(false)

    const acpHarness = makeProvider({
      id: 'claude-code-acp',
      driver: 'claude-code',
      display_name: 'Claude Code',
      connection_kind: 'acp' as ModelProviderInstanceDto['connection_kind'],
      has_api_key: false,
    })
    expect(isProviderEffectivelyConfigured(acpHarness)).toBe(true)

    const receipt = makeReceipt([
      { id: 'model_route', status: 'ready' },
      { id: 'model_secret', status: 'blocked', reason_code: 'missing_api_key' },
      { id: 'model_probe', status: 'unavailable', reason_code: 'probe_not_run' },
    ])

    expect(shouldAutoOpenOobe(receipt, [devSeed], createDefaultOobeState())).toBe(true)
  })

  it('stays closed for already configured users even when model_probe is unavailable (probe_not_run)', () => {
    const realProvider = makeProvider({ id: 'openai-prod', has_api_key: true })
    const receipt = makeReceipt([
      { id: 'model_route', status: 'ready' },
      { id: 'model_secret', status: 'ready' },
      { id: 'model_probe', status: 'unavailable', reason_code: 'probe_not_run' },
    ])

    const summary = evaluateReadinessForOobe(receipt, [realProvider])
    expect(summary.ready).toBe(true)
    expect(summary.modelProbeStatus).toBe('not_run')
    expect(shouldAutoOpenOobe(receipt, [realProvider], createDefaultOobeState())).toBe(false)
  })

  it('respects skippedAt for auto-open while allowing manual replay anytime', () => {
    const receipt = makeReceipt([{ id: 'model_route', status: 'blocked' }])
    const skippedState = {
      ...createDefaultOobeState(),
      firstRun: { skippedAt: '2026-11-22T00:00:00Z', resumeStep: 'provider' as const },
    }

    expect(shouldAutoOpenOobe(receipt, [], skippedState)).toBe(false)

    const { open, currentStep } = useOobe()
    replayOobeWizard()
    expect(open.value).toBe(true)
    expect(currentStep.value).toBe('welcome')
    expect(getNextStep('welcome')).toBe('provider')
    expect(getPrevStep('provider')).toBe('welcome')
  })

  it('classifies probe errors distinctly across auth, 404, network, rate-limit, and not_run', () => {
    expect(classifyProbeError({ id: 'model_probe', status: 'unavailable', reason_code: 'probe_not_run' }).kind).toBe('not_run')
    expect(classifyProbeError({ id: 'model_probe', status: 'blocked', reason: 'HTTP 401 unauthorized' }).kind).toBe('auth')
    expect(classifyProbeError({ id: 'model_probe', status: 'blocked', reason: 'HTTP 404 model_not_found' }).kind).toBe('model_not_found')
    expect(classifyProbeError({ id: 'model_probe', status: 'blocked', reason: 'HTTP 429 rate limit exceeded' }).kind).toBe('rate_limit')
    expect(classifyProbeError(new Error('connect ECONNREFUSED 127.0.0.1:11434')).kind).toBe('network')
  })

  it('defers desktop pet & companion capsule activation until wizard closes', async () => {
    const saveCompanionMode = vi.fn().mockResolvedValue({ companion_mode_enabled: true })
    const listDownloaded = vi.fn().mockResolvedValue([{ slug: 'neko', enabled: false }])
    const setEnabled = vi.fn().mockResolvedValue({ slug: 'neko', enabled: true })

    const prevTinadec = window.tinadec
    Object.defineProperty(window, 'tinadec', {
      configurable: true,
      value: {
        saveCompanionMode,
        pets: { listDownloaded, setEnabled },
      },
    })

    try {
      openOobeWizard('workspace')
      queueDeferredHabit('companionCapsule', true)
      queueDeferredHabit('desktopPet', true)

      expect(saveCompanionMode).not.toHaveBeenCalled()
      expect(setEnabled).not.toHaveBeenCalled()

      await closeOobeWizard('complete')

      expect(saveCompanionMode).toHaveBeenCalledWith(true)
      expect(setEnabled).toHaveBeenCalledWith('neko', true)
    } finally {
      Object.defineProperty(window, 'tinadec', { configurable: true, value: prevTinadec })
    }
  })
})
