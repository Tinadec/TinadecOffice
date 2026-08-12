import type {
  LayoutScope,
  LayoutSource,
  UieLayoutSnapshot,
  UiePageId,
  UieSlotId,
  UieStackId,
} from './types'

// ---------------------------------------------------------------------------
// Layout commands + envelope.
//
// Every layout mutation goes through the command bus as an envelope carrying
// `source` and `expectedRevision`. The reducer validates the envelope, produces
// the next snapshot AND the inverse command (for undo).
// ---------------------------------------------------------------------------

export type UieCommand =
  | {
      type: 'openCard'
      scope: LayoutScope
      descriptorId: string
      slotId?: UieSlotId
      stackId?: UieStackId
      /** Target dock pane (when the column hosts a dock). Defaults to main pane. */
      paneId?: string
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
      toSlotId?: UieSlotId
      toStackId?: UieStackId
      toIndex?: number
    }
  | {
      type: 'moveStack'
      scope: LayoutScope
      slotId: UieSlotId
      toSlotId: UieSlotId
      toIndex?: number
    }
  | { type: 'swapColumns'; scope: LayoutScope; a: UieSlotId; b: UieSlotId }
  | {
      type: 'splitStack'
      scope: LayoutScope
      slotId: UieSlotId
      instanceId?: string
      ratio?: number
    }
  | { type: 'mergeStack'; scope: LayoutScope; slotId: UieSlotId }
  | { type: 'resizeColumn'; scope: LayoutScope; slotId: UieSlotId; width: number }
  | { type: 'resizeSplit'; scope: LayoutScope; slotId: UieSlotId; ratio: number }
  | { type: 'collapseColumn'; scope: LayoutScope; slotId: UieSlotId; collapsed: boolean }
  | {
      type: 'updateCardGrid'
      scope: LayoutScope
      instanceId: string
      x?: number
      y?: number
      w?: number
      h?: number
    }
  | {
      type: 'splitDockPane'
      scope: LayoutScope
      slotId: UieSlotId
      /** The target pane to split. */
      paneId: string
      /** `row` = left/right split; `column` = top/bottom split. */
      dir: 'row' | 'column'
      /** `start` puts the new pane first (left/top); `end` second (right/bottom). */
      place: 'start' | 'end'
      /** The card instance moved into the new pane. */
      instanceId: string
      ratio?: number
      /** Explicit new pane id (tests only). */
      paneId2?: string
      /** Explicit new split id (tests only). */
      splitId?: string
    }
  | { type: 'mergeDockPane'; scope: LayoutScope; slotId: UieSlotId; paneId: string }
  /** Merge every dock pane back into a single stack and clear the dock. */
  | { type: 'mergeDockColumn'; scope: LayoutScope; slotId: UieSlotId }
  | {
      type: 'moveCardToDockPane'
      scope: LayoutScope
      instanceId: string
      toPaneId: string
      toIndex?: number
    }
  | { type: 'resizeDockSplit'; scope: LayoutScope; slotId: UieSlotId; splitId: string; ratio: number }
  | { type: 'applyPreset'; scope: LayoutScope; presetId: UiePageId }
  | { type: 'resetScope'; scope: LayoutScope }
  /** Internal — only the reducer / undo may emit this. Not dispatched externally. */
  | { type: '__restoreSnapshot'; scope: LayoutScope; snapshot: UieLayoutSnapshot }

export interface UieCommandEnvelope {
  command: UieCommand
  source: LayoutSource
  /** Must equal the current snapshot.revision, otherwise the reducer rejects. */
  expectedRevision: number
}

/** Internal command that external dispatchers must not emit. */
export const INTERNAL_COMMANDS: ReadonlySet<string> = new Set(['__restoreSnapshot'])

/** Reject AI-sourced commands this round (reserved entry point for later). */
export const REJECTED_SOURCES: ReadonlySet<string> = new Set(['ai'])

export function isInternalCommand(command: UieCommand): boolean {
  return INTERNAL_COMMANDS.has(command.type)
}

export function isRejectedSource(source: LayoutSource): boolean {
  return REJECTED_SOURCES.has(source)
}
