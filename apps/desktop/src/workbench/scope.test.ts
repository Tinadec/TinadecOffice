import { describe, it, expect } from 'vitest'
import { writeScopeFor, scopeKey, scopeIsReadableFor, resolveReadScope } from './scope'
import type { LayoutScope } from './types'

describe('layout scope', () => {
  it('write scope is workspace-page when a project is active', () => {
    expect(writeScopeFor('home', 'proj-1')).toEqual({ kind: 'workspace-page', projectId: 'proj-1', pageId: 'home' })
  })

  it('write scope is page when no project is active', () => {
    expect(writeScopeFor('home', null)).toEqual({ kind: 'page', pageId: 'home' })
  })

  it('scopeKey is unique per kind', () => {
    const g = scopeKey({ kind: 'global' })
    const p = scopeKey({ kind: 'page', pageId: 'home' })
    const w = scopeKey({ kind: 'workspace-page', projectId: 'p1', pageId: 'home' })
    const w2 = scopeKey({ kind: 'workspace-page', projectId: 'p2', pageId: 'home' })
    expect(new Set([g, p, w, w2]).size).toBe(4)
  })

  it('scopeIsReadableFor matches the same page', () => {
    const p: LayoutScope = { kind: 'page', pageId: 'home' }
    expect(scopeIsReadableFor(p, 'home', null)).toBe(true)
    expect(scopeIsReadableFor(p, 'code', null)).toBe(false)
  })

  it('workspace-page scope only readable for the matching project', () => {
    const w: LayoutScope = { kind: 'workspace-page', projectId: 'p1', pageId: 'home' }
    expect(scopeIsReadableFor(w, 'home', 'p1')).toBe(true)
    expect(scopeIsReadableFor(w, 'home', 'p2')).toBe(false)
  })

  it('resolveReadScope picks workspace > page > global, most specific wins', () => {
    const has = (scope: LayoutScope) => scopeKey(scope) === 'workspace:p1:home'
    const resolved = resolveReadScope('home', 'p1', has)
    expect(resolved).toEqual({ kind: 'workspace-page', projectId: 'p1', pageId: 'home' })
  })

  it('resolveReadScope falls back to page when workspace missing', () => {
    const present = new Set(['page:home'])
    const has = (scope: LayoutScope) => present.has(scopeKey(scope))
    const resolved = resolveReadScope('home', 'p1', has)
    expect(resolved).toEqual({ kind: 'page', pageId: 'home' })
  })

  it('resolveReadScope falls back to global when page missing', () => {
    const present = new Set(['global'])
    const has = (scope: LayoutScope) => present.has(scopeKey(scope))
    const resolved = resolveReadScope('home', null, has)
    expect(resolved).toEqual({ kind: 'global' })
  })

  it('resolveReadScope returns null when nothing stored (caller falls back to preset)', () => {
    const has = () => false
    expect(resolveReadScope('home', null, has)).toBeNull()
  })
})
