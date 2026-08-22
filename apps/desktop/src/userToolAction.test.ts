import { describe, expect, it } from 'vitest'
import {
  isUserToolActionTerminal,
  userToolActionIdempotencyKey,
  userToolActionApprovalStatus,
  userToolActionNeedsDecision,
  userToolActionStatusMessage,
  userToolActionToApproval,
  userToolApprovalId,
} from './userToolAction'
import type { UserToolActionDto } from './api'

const action = (overrides: Partial<UserToolActionDto> = {}): UserToolActionDto => ({
  id: 'action-1',
  tenant_id: 'tenant-1',
  workspace_id: 'workspace-1',
  project_id: 'project-1',
  principal_id: 'principal-1',
  tool_id: 'write_file',
  status: 'awaiting_approval',
  risk: 'high',
  mutates_workspace: true,
  requires_approval: true,
  created_at: '2026-08-22T00:00:00Z',
  updated_at: '2026-08-22T00:00:00Z',
  ...overrides,
})

describe('user tool action projection', () => {
  it('prefers action approval and then permission identities', () => {
    expect(userToolApprovalId(action({ action_approval_id: 'approval-1', permission_request_id: 'permission-1' }))).toBe('approval-1')
    expect(userToolApprovalId(action({ permission_request_id: 'permission-1' }))).toBe('permission-1')
    expect(userToolApprovalId(action())).toBe('action-1')
  })

  it('maps action lifecycle to approval-panel status without exposing nonce material', () => {
    expect(userToolActionApprovalStatus(action({ status: 'awaiting_user' }))).toBe('pending')
    expect(userToolActionApprovalStatus(action({ status: 'completed' }))).toBe('approved')
    expect(userToolActionApprovalStatus(action({ status: 'blocked' }))).toBe('rejected')
    expect(isUserToolActionTerminal('outcome_unknown')).toBe(true)
  })

  it('projects context and uses permission kind before action approval exists', () => {
    const projected = userToolActionToApproval(action({ permission_request_id: 'permission-1' }), 'Save file', {
      sessionId: 'session-1',
      cwd: 'D:/repo',
    })
    expect(projected).toMatchObject({
      id: 'permission-1',
      kind: 'permission',
      session_id: 'session-1',
      command: 'write_file',
      cwd: 'D:/repo',
      status: 'pending',
    })
    expect(projected).not.toHaveProperty('nonce')
  })

  it('creates the same bounded key for equivalent payloads regardless of key order', async () => {
    const first = await userToolActionIdempotencyKey('desktop:file-tree', {
      cwd: 'D:/repo',
      operation: 'rename',
      source: 'a.txt',
      target: 'b.txt',
    })
    const second = await userToolActionIdempotencyKey('desktop:file-tree', {
      target: 'b.txt',
      source: 'a.txt',
      operation: 'rename',
      cwd: 'D:/repo',
    })
    expect(first).toBe(second)
    expect(first.length).toBeLessThanOrEqual(256)
  })

  it('changes the key when the write payload changes', async () => {
    const first = await userToolActionIdempotencyKey('desktop:code-editor:save', {
      cwd: 'D:/repo',
      file_path: 'a.txt',
      file_hash: 'old-hash',
      content: 'one',
    })
    const second = await userToolActionIdempotencyKey('desktop:code-editor:save', {
      cwd: 'D:/repo',
      file_path: 'a.txt',
      file_hash: 'old-hash',
      content: 'two',
    })
    expect(first).not.toBe(second)
  })

  it('recognizes only governance decision states as approval waits', () => {
    expect(userToolActionNeedsDecision('awaiting_delegate')).toBe(true)
    expect(userToolActionNeedsDecision('awaiting_user')).toBe(true)
    expect(userToolActionNeedsDecision('awaiting_approval')).toBe(true)
    expect(userToolActionNeedsDecision('snapshot_required')).toBe(false)
    expect(userToolActionStatusMessage(action({ status: 'outcome_unknown' }), 'Save file')).toContain('unknown')
  })
})
