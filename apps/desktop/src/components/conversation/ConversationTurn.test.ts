// @vitest-environment happy-dom
import { beforeEach, describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import type { CorrectionTrace } from '@/lib/conversation/correction'
import type { RunActivity } from '@/lib/conversation/reducer'
import type { ConversationTurn as Turn } from '@/lib/conversation/turns'
import ConversationTurn from './ConversationTurn.vue'

/**
 * A run spends most of its life before its first answer frame: measured on five real runs the
 * gap between a run starting and `answer.started` was 69-86% of the run. The panel must show
 * that it is working during that stretch instead of looking idle and then materialising a
 * whole answer.
 */
const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })
const global = { plugins: [i18n] }

function turn(options: {
  status: string
  streamingText?: string | null
  answer?: string | null
  draft?: boolean
  correction?: CorrectionTrace
}): Turn {
  return {
    id: 'turn-1',
    user: null,
    runs: [{
      run: { id: 'run-1', status: options.status },
      activity: options.correction ? activity(options.correction) : null,
      streamingText: options.streamingText ?? null,
    }],
    answers: options.answer
      ? [{
          id: 'msg-1', session_id: 's', role: 'assistant', content: options.answer,
          run_id: 'run-1', created_at: '2026-10-18T00:00:00Z',
        } as Turn['answers'][number]]
      : [],
    phase: 'running',
    startedAt: '2026-10-18T00:00:00Z',
    endedAt: null,
  }
}

function correction(requirement: CorrectionTrace['completion']['requirement'], question = false): CorrectionTrace {
  return {
    episodeId: 'episode-1', runId: 'run-1', parentRunId: null, revision: 1,
    phase: 'OBSERVE', status: 'investigating', goal: 'fix', symptom: 'broken', doneWhen: 'works',
    summary: '', reason: '', rootCause: 'UNKNOWN', recheck: null, confidence: 'low',
    intervention: null, prediction: null, verdict: null, fix: null, verify: null,
    hypotheses: [], evidence: [], interventions: [],
    completion: {
      requirement, authority: requirement === 'user' ? 'user' : 'machine', status: 'blocked',
      modifiedArtifacts: [], verifiedArtifacts: [], strongestCheck: null,
      failedBeforeExecutionId: null, passedAfterExecutionId: null,
      unresolvedReason: null, verifiedAt: null,
    },
    openQuestions: question ? [{
      id: 'q-1', question: 'Which environment?', blocking: true, options: [],
      answer: null, askedAt: null, answeredAt: null,
    }] : [],
    createdAt: null, updatedAt: null,
  }
}

function activity(trace: CorrectionTrace): RunActivity {
  return {
    runId: 'run-1', version: 1, entries: [], tasks: new Map(),
    firstAt: null, lastAt: null, failure: null, review: null, correction: trace,
    byId: new Map(), seen: new Set(), executionByApproval: new Map(),
  }
}

/**
 * The turn template is driven by the vapour-capable component tree; rendering it here exercises
 * the real template rather than a copy of its conditions.
 */
function placeholder(wrapper: ReturnType<typeof mount>) {
  return wrapper.find('[data-testid="answer-drafting"]')
}

describe('conversation turn drafting placeholder', () => {
  beforeEach(() => {
    // Nothing global to reset; kept so the file has one place to add setup later.
  })

  it('shows it is working while a live run has produced no answer text yet', () => {
    const wrapper = mount(ConversationTurn, {
      props: { turn: turn({ status: 'executing' }), revision: 1, approvals: [] },
      global,
    })
    expect(placeholder(wrapper).exists()).toBe(true)
    expect(placeholder(wrapper).attributes('role')).toBe('status')
    expect(placeholder(wrapper).find('svg.lucide-pen-line').exists()).toBe(true)
  })

  it('gives way to the streaming answer as soon as text arrives', () => {
    const wrapper = mount(ConversationTurn, {
      props: { turn: turn({ status: 'executing', streamingText: 'partial answer' }), revision: 1, approvals: [] },
      global,
    })
    expect(placeholder(wrapper).exists()).toBe(false)
    expect(wrapper.find('[data-testid="chat-streaming-reply"]').exists()).toBe(true)
  })

  it('shows planning, not drafting, while the run is still planning', () => {
    const wrapper = mount(ConversationTurn, {
      props: { turn: turn({ status: 'planning' }), revision: 1, approvals: [] },
      global,
    })
    expect(placeholder(wrapper).text()).toBe('Planning')
    expect(placeholder(wrapper).find('svg.lucide-list-checks').exists()).toBe(true)
  })

  it('disappears once the run settles, leaving the committed answer', () => {
    const wrapper = mount(ConversationTurn, {
      props: { turn: turn({ status: 'completed', answer: 'the answer' }), revision: 2, approvals: [] },
      global,
    })
    expect(placeholder(wrapper).exists()).toBe(false)
    expect(wrapper.find('[data-testid="assistant-answer"]').exists()).toBe(true)
  })

  it('does not appear for a settled run that produced no answer', () => {
    // A cancelled run must not keep claiming to work: the placeholder is tied to a live status.
    const wrapper = mount(ConversationTurn, {
      props: { turn: turn({ status: 'cancelled' }), revision: 2, approvals: [] },
      global,
    })
    expect(placeholder(wrapper).exists()).toBe(false)
  })

  // A parked run is live but not producing. Showing the drafting shimmer here covered an
  // escalated run that was waiting for a decision with a permanent "drafting" animation;
  // the header pill that used to carry the real signal is gone, so the turn states it.
  for (const status of ['awaiting_user', 'awaiting_approval', 'awaiting_delegate']) {
    it(`shows a static waiting line, not the drafting shimmer, when parked as ${status}`, () => {
      const wrapper = mount(ConversationTurn, {
        props: { turn: turn({ status }), revision: 1, approvals: [] },
        global,
      })
      expect(placeholder(wrapper).exists()).toBe(false)
      const waiting = wrapper.find('[data-testid="turn-waiting"]')
      expect(waiting.exists()).toBe(true)
      expect(waiting.attributes('role')).toBe('status')
      expect(waiting.text()).toBe({
        awaiting_user: 'Waiting for your decision',
        awaiting_approval: 'Waiting for your approval',
        awaiting_delegate: 'Waiting for you to delegate',
      }[status])
    })
  }

  it('leaves the waiting line to the supervision gate when Core sent a decision to make', () => {
    const wrapper = mount(ConversationTurn, {
      props: {
        turn: turn({ status: 'awaiting_user' }),
        revision: 1,
        approvals: [],
        supervisionReviews: { 'run-1': { runId: 'run-1', reasons: ['needs a person'], options: ['continue', 'correct', 'cancel'] } },
      },
      global,
    })
    expect(wrapper.find('[data-testid="turn-waiting"]').exists()).toBe(false)
  })

  it('offers only user confirmation while a user-authority correction is parked', () => {
    const wrapper = mount(ConversationTurn, {
      props: {
        turn: turn({ status: 'awaiting_user', correction: correction('user') }),
        revision: 1,
        approvals: [],
      },
      global,
    })
    const feedback = wrapper.find('[data-testid="correction-feedback"]')
    expect(feedback.exists()).toBe(true)
    expect(feedback.text()).toContain('Resolved')
    expect(feedback.text()).not.toContain('Still reproduces')
  })

  it('shows the clarification gate without post-completion feedback', () => {
    const wrapper = mount(ConversationTurn, {
      props: {
        turn: turn({ status: 'awaiting_user', correction: correction('diagnostics', true) }),
        revision: 1,
        approvals: [],
      },
      global,
    })
    expect(wrapper.find('[data-testid="correction-question-gate"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="correction-feedback"]').exists()).toBe(false)
  })

  it('offers both outcomes only after a correction produced its answer', () => {
    const wrapper = mount(ConversationTurn, {
      props: {
        turn: turn({ status: 'completed', answer: 'fixed', correction: correction('user') }),
        revision: 2,
        approvals: [],
      },
      global,
    })
    const feedback = wrapper.find('[data-testid="correction-feedback"]')
    expect(feedback.exists()).toBe(true)
    expect(feedback.text()).toContain('Still reproduces')
    expect(feedback.text()).toContain('Resolved')
  })
})
