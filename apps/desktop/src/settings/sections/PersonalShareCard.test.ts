// @vitest-environment happy-dom
/**
 * Share card dialog tests.
 *
 * The painted pixels are out of reach here (happy-dom has no 2D context), so
 * these pin the contract around the picture instead: the canvas the user is
 * shown, the three controls, the privacy switch and the fact that a missing
 * canvas ends in a report rather than a thrown promise. What the picture *says*
 * is pinned in `lib/shareCard.test.ts`, against the same model the painter eats.
 */
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import PersonalShareCard from './PersonalShareCard.vue'
import type { ShareUsage } from '@/lib/shareCard'

const statusError = vi.fn()
const notifySuccess = vi.fn()

const originalToBlob = HTMLCanvasElement.prototype.toBlob

beforeEach(() => {
  statusError.mockClear()
  notifySuccess.mockClear()
  // happy-dom's toBlob never invokes its callback, which would leave `copyImage`
  // pending forever. A canvas that yields no PNG (a tainted one does the same)
  // is the branch worth pinning: the dialog must report, not hang or claim
  // success.
  HTMLCanvasElement.prototype.toBlob = function toBlob(callback: BlobCallback): void {
    callback(null)
  } as typeof HTMLCanvasElement.prototype.toBlob
})

afterEach(() => {
  HTMLCanvasElement.prototype.toBlob = originalToBlob
})

vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()),
  useI18n: () => ({ t: (key: string) => key, locale: { value: 'zh-CN' } }),
}))

vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({
    notify: { success: notifySuccess, error: vi.fn() },
    status: { error: statusError },
  }),
}))

const USAGE: ShareUsage = {
  yearlyTokens: 20_000,
  peakDailyTokens: 3751,
  currentStreakDays: 16,
  totalActiveDays: 16,
  dailyTokens: { '2026-10-3': 100 },
}

function mountCard(usage: ShareUsage | null = USAGE) {
  return mount(PersonalShareCard, { props: { usage } })
}

function actions(wrapper: ReturnType<typeof mountCard>) {
  return wrapper.findAll('.personal-share-action')
}

describe('PersonalShareCard', () => {
  it('shows the card, says so when this environment cannot paint it, and offers three controls', () => {
    const wrapper = mountCard()

    const canvas = wrapper.find('.personal-share-canvas')
    expect(canvas.exists()).toBe(true)
    expect(canvas.attributes('role')).toBe('img')
    expect(canvas.attributes('aria-label')).toBe('settings.personalShareTitle')
    // happy-dom has no 2D context, so the dialog reports that instead of
    // presenting an empty frame as if it were the card.
    expect(wrapper.find('.personal-share-fallback').exists()).toBe(true)

    expect(actions(wrapper)).toHaveLength(3)
    expect(actions(wrapper).map((button) => button.attributes('aria-label'))).toEqual([
      'settings.personalShareHideProfile',
      'settings.personalShareCopy',
      'settings.personalShareDownload',
    ])
  })

  it('survives a usage aggregate that has not loaded yet', () => {
    const wrapper = mountCard(null)

    expect(wrapper.find('.personal-share-canvas').exists()).toBe(true)
    expect(actions(wrapper)).toHaveLength(3)
  })

  it('turns the privacy switch into aria-pressed and back', async () => {
    const wrapper = mountCard()
    const [toggle] = actions(wrapper)

    expect(toggle!.attributes('aria-pressed')).toBe('false')
    expect(toggle!.attributes('title')).toBe('settings.personalShareHideProfile')

    await toggle!.trigger('click')
    expect(toggle!.attributes('aria-pressed')).toBe('true')
    expect(toggle!.attributes('title')).toBe('settings.personalShareShowProfile')

    await toggle!.trigger('click')
    expect(toggle!.attributes('aria-pressed')).toBe('false')
  })

  it('reports a failed copy and download instead of rejecting', async () => {
    const wrapper = mountCard()
    const [, copy, download] = actions(wrapper)

    await copy!.trigger('click')
    await flushPromises()
    expect(statusError).toHaveBeenCalledWith(
      expect.objectContaining({ key: 'personal-share', message: 'settings.personalShareCopyFailed' }),
    )

    await download!.trigger('click')
    await flushPromises()
    expect(statusError).toHaveBeenCalledWith(
      expect.objectContaining({ key: 'personal-share', message: 'settings.personalShareDownloadFailed' }),
    )
    expect(notifySuccess).not.toHaveBeenCalled()
  })

  it('closes from the close button and from a backdrop click', async () => {
    const wrapper = mountCard()

    await wrapper.find('.personal-share-close-row button').trigger('click')
    await wrapper.find('.personal-share-overlay').trigger('click')
    // A click inside the column must not close the dialog.
    await wrapper.find('.personal-share-actions').trigger('click')

    const closes = wrapper.emitted('close')
    expect(closes).toHaveLength(2)
  })
})
