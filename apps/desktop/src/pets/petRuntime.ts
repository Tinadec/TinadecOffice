export const PETDEX_COLUMNS = 8
export const PETDEX_MIN_ROWS = 9
export const PETDEX_BASE_FRAME_WIDTH = 192
export const PETDEX_BASE_FRAME_HEIGHT = 208
export const PETDEX_FRAME_ASPECT = PETDEX_BASE_FRAME_WIDTH / PETDEX_BASE_FRAME_HEIGHT

export const PET_STATES = {
  idle: { row: 0, frames: 6, durationMs: 1100 },
  'running-right': { row: 1, frames: 8, durationMs: 1060 },
  'running-left': { row: 2, frames: 8, durationMs: 1060 },
  waving: { row: 3, frames: 4, durationMs: 700 },
  jumping: { row: 4, frames: 5, durationMs: 840 },
  failed: { row: 5, frames: 8, durationMs: 1220 },
  waiting: { row: 6, frames: 6, durationMs: 1010 },
  running: { row: 7, frames: 6, durationMs: 820 },
  review: { row: 8, frames: 6, durationMs: 1030 },
} as const

export type PetState = keyof typeof PET_STATES

export interface PetSpriteLayout {
  columns: number
  rows: number
  frameWidth: number
  frameHeight: number
}

export interface PetFrame {
  row: number
  column: number
  complete: boolean
}

export function parsePetSprite(width: number, height: number): PetSpriteLayout {
  if (!Number.isInteger(width) || !Number.isInteger(height) || width <= 0 || height <= 0) {
    throw new Error('Pet spritesheet dimensions are invalid')
  }
  const frameWidth = width / PETDEX_COLUMNS
  const frameHeight = frameWidth / PETDEX_FRAME_ASPECT
  const rows = height / frameHeight
  if (!Number.isInteger(frameWidth) || !Number.isInteger(frameHeight) || !Number.isInteger(rows) || rows < PETDEX_MIN_ROWS) {
    throw new Error('Pet spritesheet must use the Petdex 8-column frame grid')
  }
  return { columns: PETDEX_COLUMNS, rows, frameWidth, frameHeight }
}

export function petFrameAt(state: PetState, elapsedMs: number): PetFrame {
  const animation = PET_STATES[state]
  const elapsed = Math.max(0, elapsedMs)
  const cycleElapsed = elapsed % animation.durationMs
  const column = Math.min(animation.frames - 1, Math.floor(cycleElapsed * animation.frames / animation.durationMs))
  return { row: animation.row, column, complete: elapsed >= animation.durationMs }
}

export function petSourceRect(layout: PetSpriteLayout, frame: Pick<PetFrame, 'row' | 'column'>) {
  return {
    x: frame.column * layout.frameWidth,
    y: frame.row * layout.frameHeight,
    width: layout.frameWidth,
    height: layout.frameHeight,
  }
}
