import { describe, expect, it } from 'vitest'
import { PET_STATES, parsePetSprite, petFrameAt, petSourceRect } from './petRuntime'

describe('Petdex pet runtime', () => {
  it('parses classic and extended Petdex grids', () => {
    expect(parsePetSprite(1536, 1872)).toEqual({ columns: 8, rows: 9, frameWidth: 192, frameHeight: 208 })
    expect(parsePetSprite(1536, 2288).rows).toBe(11)
    expect(() => parsePetSprite(1500, 1872)).toThrow('Petdex 8-column')
  })

  it('loops only through each state active cells', () => {
    expect(petFrameAt('idle', 0).column).toBe(0)
    expect(petFrameAt('idle', 1099).column).toBe(5)
    expect(petFrameAt('idle', 1100).column).toBe(0)
    expect(petFrameAt('idle', 60_000).column).toBeLessThan(PET_STATES.idle.frames)
  })

  it('uses the canonical jumping row and reports a completed cycle', () => {
    const frame = petFrameAt('jumping', PET_STATES.jumping.durationMs)
    expect(frame).toEqual({ row: 4, column: 0, complete: true })
    expect(petSourceRect(parsePetSprite(1536, 1872), { row: 4, column: 3 })).toEqual({
      x: 576,
      y: 832,
      width: 192,
      height: 208,
    })
  })
})
