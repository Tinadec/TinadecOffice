// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { describe, expect, it } from 'vitest'
import en from '@/locales/en'
import type { CorrectionTrace as Trace } from '@/lib/conversation/correction'
import CorrectionTrace from './CorrectionTrace.vue'

const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })

function trace(): Trace {
  return {
    episodeId: 'episode-1',
    runId: 'run-1',
    parentRunId: null,
    revision: 9,
    phase: 'CONVERGE',
    status: 'investigating',
    goal: 'Fix the test',
    symptom: 'npm test fails',
    doneWhen: 'npm test passes',
    summary: '',
    reason: '',
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
}

describe('correction trace completion authority', () => {
  it('shows completion ready even when an older snapshot status still says investigating', () => {
    const wrapper = mount(CorrectionTrace, {
      props: { trace: trace() },
      global: { plugins: [i18n] },
    })

    expect(wrapper.get('.conversation-correction-badge').text()).toBe('Evidence complete')
    wrapper.unmount()
  })
})
