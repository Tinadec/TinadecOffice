import type {
  LayoutScope,
  LayoutSource,
  WorkbenchLayoutSnapshot,
  WorkbenchPageId,
  WorkbenchSlotId,
  WorkbenchStackId,
} from './types'

// ---------------------------------------------------------------------------
// Layout commands + envelope.
//
// Every layout mutation goes through the command bus as an envelope carrying
// `source` and `expectedRevision`. The reducer validates the envelope, produces
// the next snapshot AND the inverse command (for undo).
// ---------------------------------------------------------------------------

export type WorkbenchCommand =
  | {
      type: 'openCard'
      scope: LayoutScope
      descriptorId: string
      slotId?: WorkbenchSlotId
      stackId?: WorkbenchStackId
      toIndex?: number
      title?: string
      state?: Record<string, unknown>
      /** Optional explicit instance id (used by undo/reopen to preserve identity). */
      instanceId?: string
    }
  | { type: 'closeCard'; scope: LayoutScope; instanceId: string }
  | { type: 'activateCard'; scope: LayoutScope; instanceId: string }
  | {
      type: 'moveCard'
      scope: LayoutScope
      instanceId: string
      toSlotId?: WorkbenchSlotId
      toStackId?: WorkbenchStackId
      toIndex?: number
    }
  | {
      type: 'moveStack'
      scope: LayoutScope
      slotId: WorkbenchSlotId
      toSlotId: WorkbenchSlotId
      toIndex?: number
    }
  | { type: 'swapColumns'; scope: LayoutScope; a: WorkbenchSlotId; b: WorkbenchSlotId }
  | {
      type: 'splitStack'
      scope: LayoutScope
      slotId: WorkbenchSlotId
      instanceId?: string
      ratio?: number
    }
  | { type: 'mergeStack'; scope: LayoutScope; slotId: WorkbenchSlotId }
  | { type: 'resizeColumn'; scope: LayoutScope; slotId: WorkbenchSlotId; width: number }
  | { type: 'resizeSplit'; scope: LayoutScope; slotId: WorkbenchSlotId; ratio: number }
  | { type: 'collapseColumn'; scope: LayoutScope; slotId: WorkbenchSlotId; collapsed: boolean }
  | { type: 'applyPreset'; scope: LayoutScope; presetId: WorkbenchPageId }
  | { type: 'resetScope'; scope: LayoutScope }
  /** Internal — only the reducer / undo may emit this. Not dispatched externally. */
  | { type: '__restoreSnapshot'; scope: LayoutScope; snapshot: WorkbenchLayoutSnapshot }

export interface WorkbenchCommandEnvelope {
  command: WorkbenchCommand
  source: LayoutSource
  /** Must equal the current snapshot.revision, otherwise the reducer rejects. */
  expectedRevision: number
}

/** Internal command that external dispatchers must not emit. */
export const INTERNAL_COMMANDS: ReadonlySet<string> = new Set(['__restoreSnapshot'])

/** Reject AI-sourced commands this round (reserved entry point for later). */
export const REJECTED_SOURCES: ReadonlySet<string> = new Set(['ai'])

export function isInternalCommand(command: WorkbenchCommand): boolean {
  return INTERNAL_COMMANDS.has(command.type)
}

export function isRejectedSource(source: LayoutSource): boolean {
  return REJECTED_SOURCES.has(source)
}
