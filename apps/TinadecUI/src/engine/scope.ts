import type { LayoutScope, UiePageId } from './types'

// ---------------------------------------------------------------------------
// Layout scope resolution.
//
// Write scope (runtime):
//   - project-scoped page + active project -> { kind: 'workspace-page', projectId, pageId }
//   - otherwise                            -> { kind: 'page', pageId }
//
// Read scope (persistence, "most specific valid wins"):
//   workspace-page(projectId, pageId) > page(pageId) > global > built-in preset.
//
// A project without its own layout therefore inherits the page-wide layout (the
// one used with no workspace selected); its first edit forks a project layout.
// ---------------------------------------------------------------------------

/**
 * Pages whose layout is remembered per project. Only workspace-bound pages are
 * listed: market/chatroom content is not tied to a project, so they keep one
 * page-wide layout regardless of the active project.
 */
export const PROJECT_SCOPED_PAGES: ReadonlySet<UiePageId> = new Set<UiePageId>(['home'])

/** The project a page's layout is scoped to, or null for page-wide layouts. */
export function layoutProjectFor(
  pageId: UiePageId,
  activeProjectId: string | null | undefined,
): string | null {
  return activeProjectId && PROJECT_SCOPED_PAGES.has(pageId) ? activeProjectId : null
}

export function writeScopeFor(
  pageId: UiePageId,
  activeProjectId: string | null | undefined,
): LayoutScope {
  const projectId = layoutProjectFor(pageId, activeProjectId)
  return projectId ? { kind: 'workspace-page', projectId, pageId } : { kind: 'page', pageId }
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
  pageId: UiePageId,
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
  pageId: UiePageId,
  activeProjectId: string | null | undefined,
  hasSnapshot: (scope: LayoutScope) => boolean,
): LayoutScope | null {
  const projectId = layoutProjectFor(pageId, activeProjectId)
  if (projectId) {
    const wsScope: LayoutScope = { kind: 'workspace-page', projectId, pageId }
    if (hasSnapshot(wsScope)) return wsScope
  }
  const pageScope: LayoutScope = { kind: 'page', pageId }
  if (hasSnapshot(pageScope)) return pageScope
  const globalScope: LayoutScope = { kind: 'global' }
  if (hasSnapshot(globalScope)) return globalScope
  return null
}
