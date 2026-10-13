// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import UiIslandCard from './island-card.vue';

describe('UiIslandCard', () => {
  it('renders header and footer slots only when provided', () => {
    const withoutChrome = mount(UiIslandCard, {
      slots: { default: 'body' },
    });
    expect(withoutChrome.find('.island-header').exists()).toBe(false);
    expect(withoutChrome.find('.island-footer').exists()).toBe(false);

    const withChrome = mount(UiIslandCard, {
      slots: {
        header: '<span>title</span>',
        default: 'body',
        footer: '<button>act</button>',
      },
    });
    expect(withChrome.find('.island-header').text()).toBe('title');
    expect(withChrome.find('.island-footer button').exists()).toBe(true);
  });

  it('never binds material root attributes (no data-panel-effect, no inline backdrop style)', () => {
    const wrapper = mount(UiIslandCard, {
      slots: { default: 'body' },
    });

    const card = wrapper.find('.island-card');
    expect(card.attributes('data-panel-effect')).toBeUndefined();
    expect(card.attributes('style') ?? '').not.toContain('backdrop');
  });
});
