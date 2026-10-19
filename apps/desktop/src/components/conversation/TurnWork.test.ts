// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { describe, expect, it } from 'vitest'
import en from '@/locales/en'
import type { PlanEntry, ReasoningEntry, RunActivity } from '@/lib/conversation/reducer'
import TurnWork from './TurnWork.vue'

const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })
const global = { plugins: [i18n] }

function activity(entries: RunActivity['entries']): RunActivity {
  return {
    runId: 'run-1',
    version: 1,
    entries,
    tasks: new Map(),
    firstAt: '2026-10-18T00:00:00Z',
    lastAt: '2026-10-18T00:00:03Z',
    failure: null,
    review: null,
    correction: null,
    byId: new Map(entries.map((entry) => [entry.id, entry])),
    seen: new Set(),
    executionByApproval: new Map(),
  }
}

describe('turn work phase status', () => {
  it('keeps the current plan step visible with a planning icon after the event settles', () => {
    const plan: PlanEntry = {
      kind: 'plan', id: 'plan-1', seq: 1, taskId: null, handle: null,
      steps: [{ step: 'Map the repository', status: 'in_progress' }],
      previous: null, explanation: null, at: '2026-10-18T00:00:01Z',
    }
    const wrapper = mount(TurnWork, {
      props: {
        activity: activity([plan]), nodes: [], working: true, openByDefault: true,
        runId: 'run-1', runStatus: 'executing', revision: 1,
      },
      global,
    })
    expect(wrapper.get('[data-testid="turn-status"]').text()).toBe('Map the repository')
    expect(wrapper.find('[data-testid="turn-status-icon"]').classes()).toContain('lucide-list-checks')
    wrapper.unmount()
  })

  it('keeps a settled drafting event as the current phase with a pen icon', () => {
    const drafting: ReasoningEntry = {
      kind: 'reasoning', id: 'reasoning-1', seq: 1, taskId: null, handle: null, agentName: null,
      text: '', output: 'The answer draft', status: 'completed',
      startedAt: '2026-10-18T00:00:00Z', endedAt: '2026-10-18T00:00:02Z',
    }
    const wrapper = mount(TurnWork, {
      props: {
        activity: activity([drafting]), nodes: [], working: true, openByDefault: true,
        runId: 'run-1', runStatus: 'executing', revision: 1,
      },
      global,
    })
    expect(wrapper.get('[data-testid="turn-status"]').text()).toBe('Drafting…')
    expect(wrapper.find('[data-testid="turn-status-icon"]').classes()).toContain('lucide-pen-line')
    wrapper.unmount()
  })

  it('uses a planning icon before the first plan event arrives', () => {
    const wrapper = mount(TurnWork, {
      props: {
        activity: activity([]), nodes: [], working: true, openByDefault: true,
        runId: 'run-1', runStatus: 'planning', revision: 1,
      },
      global,
    })
    expect(wrapper.get('[data-testid="turn-status"]').text()).toBe('Planning')
    expect(wrapper.find('[data-testid="turn-status-icon"]').classes()).toContain('lucide-list-checks')
    wrapper.unmount()
  })

  it('keeps the correction phase icon and label after the work settles', () => {
    const settled = activity([])
    settled.correction = {
      episodeId: 'episode-1',
      runId: 'run-1',
      parentRunId: null,
      revision: 9,
      phase: 'CONVERGE',
      status: 'ready',
      goal: 'Fix the failing test',
      symptom: 'npm test fails',
      doneWhen: 'npm test passes',
      summary: '',
      reason: 'The same focused test now passes.',
      rootCause: 'The implementation used subtraction.',
      recheck: null,
      confidence: 'high',
      intervention: null,
      prediction: null,
      verdict: null,
      fix: null,
      verify: null,
      hypotheses: [],
      evidence: [],
      interventions: [],
      completion: {
        requirement: 'test',
        authority: 'machine',
        status: 'ready',
        modifiedArtifacts: ['src/sum.js'],
        verifiedArtifacts: ['src/sum.js'],
        strongestCheck: null,
        failedBeforeExecutionId: 'test-fail',
        passedAfterExecutionId: 'test-pass',
        unresolvedReason: null,
        verifiedAt: '2026-10-18T00:00:03Z',
      },
      openQuestions: [],
      createdAt: '2026-10-18T00:00:00Z',
      updatedAt: '2026-10-18T00:00:03Z',
    }
    const wrapper = mount(TurnWork, {
      props: {
        activity: settled, nodes: [], working: false, openByDefault: false,
        runId: 'run-1', runStatus: 'completed', revision: 2,
      },
      global,
    })

    expect(wrapper.get('[data-testid="turn-correction-summary"]').text()).toBe('Verifying the result')
    expect(wrapper.find('[data-testid="turn-correction-summary-icon"]').classes()).toContain('lucide-shield-check')
    wrapper.unmount()
  })
})
