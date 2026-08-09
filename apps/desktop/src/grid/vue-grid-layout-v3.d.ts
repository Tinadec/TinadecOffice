declare module 'vue-grid-layout-v3' {
  import type { DefineComponent } from 'vue'

  export const GridLayout: DefineComponent<{
    layout?: Array<Record<string, any>>
    colNum?: number
    rowHeight?: number
    maxRows?: number
    margin?: [number, number]
    isDraggable?: boolean
    isResizable?: boolean
    isMirrored?: boolean
    useCssTransforms?: boolean
    verticalCompact?: boolean
    responsive?: boolean
    preventCollision?: boolean
  }>

  export const GridItem: DefineComponent<{
    x: number
    y: number
    w: number
    h: number
    i: string | number
    minW?: number
    minH?: number
    maxW?: number
    maxH?: number
    isDraggable?: boolean
    isResizable?: boolean
    static?: boolean
  }>
}
