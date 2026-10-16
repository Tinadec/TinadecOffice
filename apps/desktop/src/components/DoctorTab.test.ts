// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import DoctorTab from './DoctorTab.vue'

const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })

describe('DoctorTab', () => {
  it('renders the shape Core returns and words each reason from its code', () => {
    const wrapper = mount(DoctorTab, {
      // StatusPill is a Vapor SFC, which vitest cannot mount directly; its label is all this test reads.
      global: { plugins: [i18n], stubs: { StatusPill: { props: ['label'], template: '<span>{{ label }}</span>' } } },
      props: {
        readiness: {
          status: 'blocked',
          checked_at: '2026-10-16T08:00:00Z',
          items: [
            { id: 'database', status: 'ready', reason_code: 'database_ready', reason: 'Database reachable (sqlite).' },
            {
              id: 'model_secret', status: 'blocked', reason_code: 'secret_missing',
              reason: 'The provider has no API key yet.', action: 'Enter an API key for provider OpenAI in the model center.',
              data: { provider_display_name: 'OpenAI', has_key: false },
            },
            { id: 'model_probe', status: 'unavailable', reason_code: 'probe_from_a_newer_core', reason: 'Some new diagnostic.' },
          ],
        },
      },
    })

    const secret = wrapper.get('[data-item="model_secret"]')
    expect(secret.text()).toContain('Model API key')
    expect(secret.text()).toContain('Provider OpenAI has no API key yet.')
    expect(secret.get('.readiness-action').text()).toBe('Enter the API key for the provider in the model settings.')
    // Core's own wording stays reachable as the tooltip.
    expect(secret.get('p').attributes('title')).toBe('The provider has no API key yet.')
    // A code this build has no words for falls back to Core's diagnostic text.
    expect(wrapper.get('[data-item="model_probe"]').text()).toContain('Some new diagnostic.')
    expect(wrapper.findAll('.readiness-metrics span').map((span) => span.text())).toEqual(['1 ready', '1 warning', '1 blocked'])
  })
})
