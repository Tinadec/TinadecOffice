// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest'
import { defineComponent } from 'vue'
import { mount } from '@vue/test-utils'
import { recoveryActions, toErrorState, useErrorState } from './useErrorState'
import { ApiError } from '@/lib/apiError'

describe('shared error state', () => {
  it('keeps every field of a classified failure instead of collapsing it to a sentence', () => {
    const state = toErrorState(new ApiError('The registered project directory is unavailable.', 409, {
      code: 'storage_scope_unavailable',
      category: 'environment_unavailable',
      retryable: false,
      actions: ['unregister_workspace', 'retry', 'open_storage_settings'],
      trace_id: 'trace-1',
    }))
    expect(state).toMatchObject({
      details: 'The registered project directory is unavailable.\ntrace_id: trace-1',
      code: 'storage_scope_unavailable',
      category: 'environment_unavailable',
      retryable: false,
      actions: ['unregister_workspace', 'retry', 'open_storage_settings'],
      traceId: 'trace-1',
      status: 409,
    })
  })

  it('normalises plain errors, strings and objects without inventing fields', () => {
    expect(toErrorState(new Error('boom'))).toEqual({ message: 'boom', actions: [] })
    expect(toErrorState('literal')).toEqual({ message: 'literal', actions: [] })
    expect(toErrorState({ detail: 'from detail' })).toEqual({ message: 'from detail', actions: [] })
    expect(toErrorState(null, 'fallback')).toEqual({ message: 'fallback', actions: [] })
  })

  it('is reactive through the composable and clears back to null', () => {
    let api!: ReturnType<typeof useErrorState>
    const wrapper = mount(defineComponent({ setup() { api = useErrorState('fallback'); return () => null } }))
    api.set(new Error('first'))
    expect(api.message.value).toBe('first')
    api.clear()
    expect(api.error.value).toBeNull()
    // A nullish input means "nothing to show", not "show the fallback": a caller clearing on
    // success must not accidentally display an error.
    api.set(undefined)
    expect(api.error.value).toBeNull()
    // The fallback is only used when something real failed but carries no usable message.
    api.set({})
    expect(api.error.value).toEqual({ message: 'fallback', actions: [] })
    wrapper.unmount()
  })

  it('offers only actions the screen can actually perform, never a dead button', () => {
    const state = toErrorState(new ApiError('gone', 409, {
      code: 'storage_scope_unavailable',
      actions: ['unregister_workspace', 'open_storage_settings', 'retry'],
    }))
    const ran: string[] = []
    const actions = recoveryActions(state, {
      retry: () => { ran.push('retry') },
      // open_storage_settings deliberately has no handler: it must not render.
      unregister_workspace: () => { ran.push('unregister') },
    })
    expect(actions.map(a => a.kind)).toEqual(['unregister_workspace', 'retry'])
    // Labels come from the shared wording, so a section never invents its own copy.
    expect(actions.every(a => a.label.length > 0)).toBe(true)
    actions[0]!.run()
    expect(ran).toEqual(['unregister'])
  })

  it('returns no actions for an unclassified failure', () => {
    expect(recoveryActions(toErrorState(new Error('plain')), { retry: () => {} })).toEqual([])
    expect(recoveryActions(null, { retry: () => {} })).toEqual([])
  })
})
