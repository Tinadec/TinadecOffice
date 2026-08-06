import type { LayoutScope, WorkbenchPageId } from './types'

// ---------------------------------------------------------------------------
// Layout scope resolution.
//
// Write scope (runtime):
//   - active project  -> { kind: 'workspace-page', projectId, pageId }
//   - otherwise       -> { kind: 'page', pageId }
//
// Read scope (persistence, "most specific valid wins"):
//   workspace-page(projectId, pageId) > page(pageId) > global > built-in preset.
// ---------------------------------------------------------------------------

export function writeScopeFor(
  pageId: WorkbenchPageId,
  activeProjectId: string | null | undefined,
): LayoutScope {
  if (activeProjectId) {
    return { kind: 'workspace-page', projectId: activeProjectId, pageId }
  }
  return { kind: 'page', pageId }
}

export function scopeKey(scope: LayoutScope): string {
  switch (scope.kind) {
    case 'global':
      return 'global'
    case 'page':
      return `page:${scope.pageId}`
    case 'workspace-page':
      return `workspace:${scope.projectId}:${scope.pageId}`
  }
}

export function scopeIsReadableFor(
  scope: LayoutScope,
  pageId: WorkbenchPageId,
  activeProjectId: string | null | undefined,
): boolean {
  switch (scope.kind) {
    case 'global':
      return true
    case 'page':
      return scope.pageId === pageId
    case 'workspace-page':
      return scope.pageId === pageId && (activeProjectId == null || scope.projectId === activeProjectId)
  }
}

/**
 * Pick the most specific valid scope for reading a layout, given the available
 * stored scopes (keys from scopeKey). Falls back through the precedence chain.
 */
export function resolveReadScope(
  pageId: WorkbenchPageId,
  activeProjectId: string | null | undefined,
  hasSnapshot: (scope: LayoutScope) => boolean,
): LayoutScope | null {
  if (activeProjectId) {
    const wsScope: LayoutScope = { kind: 'workspace-page', projectId: activeProjectId, pageId }
    if (hasSnapshot(wsScope)) return wsScope
  }
  const pageScope: LayoutScope = { kind: 'page', pageId }
  if (hasSnapshot(pageScope)) return pageScope
  const globalScope: LayoutScope = { kind: 'global' }
  if (hasSnapshot(globalScope)) return globalScope
  return null
}
