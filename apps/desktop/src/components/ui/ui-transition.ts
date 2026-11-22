import { Transition, type FunctionalComponent, type TransitionProps } from 'vue'

/**
 * Thin functional wrapper around Vue's built-in `Transition`.
 *
 * Delegates directly to Vue's real `Transition` (returning the exact same
 * `BaseTransition` VNode with full CSS enter/leave class hooks in the renderer),
 * while avoiding `@vue/test-utils`'s name-based empty `<transition-stub>`
 * fallback triggered by this workspace's ESM `vue-shim` alias when consumers
 * mount UI primitives without `stubs: { transition: false }`.
 */
export const UiTransition: FunctionalComponent<TransitionProps> = (props, ctx) =>
  Transition(props, ctx)
