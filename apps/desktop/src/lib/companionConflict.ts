import { ApiError } from './apiError'

/**
 * A 412 from Core (`companion_revision_conflict`) means another window saved first. The caller
 * must show the conflict and re-read; repeating the same request would just fail again, and
 * writing over the newer revision is exactly what the conditional write exists to prevent.
 */
export function isCompanionRevisionConflict(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 412 || error.code === 'companion_revision_conflict')
}
