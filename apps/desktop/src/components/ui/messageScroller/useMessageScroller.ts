/**
 * Transcript scroll engine.
 *
 * Adapted from the shadcn-vue `message-scroller` registry item (MIT, unovue) at
 * `apps/v4/registry/bases/reka/ui/message-scroller`. Kept as one file rather than the
 * six-component registry split: our scroller's DOM is three elements, and the registry's
 * `MessageScroller`/`MessageScrollerProvider`/`MessageScrollerContent` wrappers only forward
 * slots and `class`.
 *
 * Why this replaced the hand-rolled follow logic in `MessageList.vue`: the old version
 * decided "am I at the bottom?" from one scroll listener and then wrote `scrollTop` on every
 * streaming delta. That is correct for the easy case and wrong for all the others — anchoring
 * a new turn near the top with a peek of the previous exchange, holding position while a
 * reply streams below an anchored turn, restoring the reader's place when older history is
 * prepended, and jumping to a message from outside the list. This engine owns those cases.
 *
 * It is headless: it does not know what a message is, only that rows carry `messageId` and
 * that a row can be a turn `scrollAnchor`.
 */
import type { ComputedRef, InjectionKey, Ref, ShallowRef } from "vue"
import { computed, getCurrentScope, inject, onMounted, onScopeDispose, provide, shallowRef, watch } from "vue"
import { classifyScroll, SCROLL_EPSILON } from "./scrollIntent"

export type MessageScrollerDefaultScrollPosition = "start" | "end" | "last-anchor"
export type MessageScrollerButtonDirection = "start" | "end"
export type MessageScrollerScrollAlign = "start" | "center" | "end" | "nearest"

export interface MessageScrollerScrollOptions {
  align?: MessageScrollerScrollAlign
  behavior?: ScrollBehavior
  scrollMargin?: number
}

export interface MessageScrollerScrollable {
  start: boolean
  end: boolean
}

export interface MessageScrollerVisibilityState {
  currentAnchorId: string | null
  visibleMessageIds: string[]
}

export interface MessageScrollerProviderProps {
  autoScroll?: boolean
  defaultScrollPosition?: MessageScrollerDefaultScrollPosition
  scrollEdgeThreshold?: number
  scrollPreviousItemPeek?: number
  scrollMargin?: number
}

const DEFAULT_SCROLL_EDGE_THRESHOLD = 8
const DEFAULT_SCROLL_PREVIOUS_ITEM_PEEK = 64
const DEFAULT_SCROLL_MARGIN = 0
const AUTOSCROLLING_TIMEOUT = 180

export const SCROLL_KEYS = new Set([
  "ArrowDown",
  "ArrowUp",
  "End",
  "Home",
  "PageDown",
  "PageUp",
  " ",
])

const EMPTY_SCROLLABLE: MessageScrollerScrollable = { start: false, end: false }
const EMPTY_VISIBLE_IDS: string[] = []
const EMPTY_VISIBILITY: MessageScrollerVisibilityState = {
  currentAnchorId: null,
  visibleMessageIds: EMPTY_VISIBLE_IDS,
}

type Mode =
  | "following-bottom"
  | "free-scrolling"
  | "anchored-to-message"
  | "settling-jump"

interface PrependRestore {
  element: HTMLElement
  viewportTop: number
}

interface PendingScrollToMessage {
  messageId: string
  options?: MessageScrollerScrollOptions
}

function scrollableEqual(a: MessageScrollerScrollable, b: MessageScrollerScrollable) {
  return a.start === b.start && a.end === b.end
}

function visibilityEqual(a: MessageScrollerVisibilityState, b: MessageScrollerVisibilityState) {
  if (a.currentAnchorId !== b.currentAnchorId || a.visibleMessageIds.length !== b.visibleMessageIds.length) {
    return false
  }
  return a.visibleMessageIds.every((id, index) => id === b.visibleMessageIds[index])
}

// ---------------------------------------------------------------------------
// DOM measurement helpers
// ---------------------------------------------------------------------------

function parseNumber(value: string | null | undefined): number {
  if (!value) return 0
  const parsed = Number.parseFloat(value)
  return Number.isFinite(parsed) ? parsed : 0
}

function getPadding(element: HTMLElement): { start: number; end: number } {
  const style = window.getComputedStyle(element)
  return {
    end: parseNumber(style.paddingBlockEnd || style.paddingBottom),
    start: parseNumber(style.paddingBlockStart || style.paddingTop),
  }
}

function getContentPadding(spacer: HTMLElement | null): { start: number; end: number } {
  const parent = spacer?.parentElement
  return parent ? getPadding(parent) : { end: 0, start: 0 }
}

function getRowGap(element: HTMLElement | null): number {
  if (!element) return 0
  const style = window.getComputedStyle(element)
  const gap = style.rowGap === "normal" ? style.gap : style.rowGap
  return parseNumber(gap)
}

function getMessageChildren(content: HTMLElement, spacer: HTMLElement | null): HTMLElement[] {
  return Array.from(content.children).filter(
    (child): child is HTMLElement => child instanceof HTMLElement && child !== spacer,
  )
}

function getElementOffsetTop(element: HTMLElement, viewport: HTMLElement): number {
  return element.getBoundingClientRect().top - viewport.getBoundingClientRect().top + viewport.scrollTop
}

function getRelativeTop(element: HTMLElement, viewport: HTMLElement): number {
  return element.getBoundingClientRect().top - viewport.getBoundingClientRect().top
}

function measureContentHeight({
  content,
  spacer,
  viewport,
}: {
  content: HTMLElement
  spacer: HTMLElement | null
  viewport: HTMLElement
}): number {
  const children = getMessageChildren(content, spacer)
  const padding = getPadding(content)
  const viewportRect = viewport.getBoundingClientRect()
  const scrollTop = viewport.scrollTop
  let height = padding.start + padding.end
  for (const child of children) {
    const rect = child.getBoundingClientRect()
    height = Math.max(height, rect.bottom - viewportRect.top + scrollTop + padding.end)
  }
  return height
}

function maxScrollTop(element: HTMLElement): number {
  return Math.max(0, element.scrollHeight - element.clientHeight)
}

function computeSpacerHeight({
  content,
  scrollTop,
  spacer,
  viewport,
}: {
  content: HTMLElement
  scrollTop: number
  spacer: HTMLElement | null
  viewport: HTMLElement
}): number {
  const contentHeight = measureContentHeight({ content, spacer, viewport })
  return scrollTop + viewport.clientHeight - contentHeight
}

function computeScrollTopForElement({
  align,
  element,
  scrollMargin,
  spacer,
  viewport,
}: {
  align: MessageScrollerScrollAlign
  element: HTMLElement
  scrollMargin: number
  spacer: HTMLElement | null
  viewport: HTMLElement
}): number {
  const offsetTop = getElementOffsetTop(element, viewport)
  const height = element.getBoundingClientRect().height
  const padding = getContentPadding(spacer)

  if (align === "center") {
    const available = Math.max(0, viewport.clientHeight - padding.start - padding.end)
    return offsetTop - padding.start - (available - height) / 2 - scrollMargin
  }
  if (align === "end") {
    return offsetTop - viewport.clientHeight + height + padding.end + scrollMargin
  }
  if (align === "nearest") {
    const bottom = offsetTop + height
    const visibleTop = viewport.scrollTop + padding.start
    const visibleBottom = viewport.scrollTop + viewport.clientHeight - padding.end
    if (offsetTop >= visibleTop && bottom <= visibleBottom) return viewport.scrollTop
    return offsetTop < visibleTop
      ? offsetTop - padding.start - scrollMargin
      : bottom - viewport.clientHeight + padding.end + scrollMargin
  }
  return offsetTop - padding.start - scrollMargin
}

function computeScrollable({
  content,
  scrollEdgeThreshold,
  spacer,
  viewport,
}: {
  content: HTMLElement | null
  scrollEdgeThreshold: number
  spacer: HTMLElement | null
  viewport: HTMLElement | null
}): MessageScrollerScrollable {
  if (!viewport || !content) return EMPTY_SCROLLABLE
  const contentHeight = measureContentHeight({ content, spacer, viewport })
  return {
    start: viewport.scrollTop > scrollEdgeThreshold,
    end: contentHeight - viewport.scrollTop - viewport.clientHeight > scrollEdgeThreshold,
  }
}

function computeVisibility({
  content,
  scrollMargin,
  scrollPreviousItemPeek,
  spacer,
  viewport,
  visibleMessageIds,
}: {
  content: HTMLElement | null
  scrollMargin: number
  scrollPreviousItemPeek: number
  spacer: HTMLElement | null
  viewport: HTMLElement | null
  visibleMessageIds: Set<string>
}): MessageScrollerVisibilityState {
  if (!content || !viewport) return EMPTY_VISIBILITY
  const viewportRect = viewport.getBoundingClientRect()
  const anchorLine = viewportRect.top + scrollMargin + scrollPreviousItemPeek
  const noIntersectionObserver = typeof IntersectionObserver === "undefined"
  const visible: string[] = []
  let currentAnchorId: string | null = null

  for (const child of getMessageChildren(content, spacer)) {
    const messageId = child.dataset.messageId
    if (!messageId) continue
    const isAnchor = child.dataset.scrollAnchor === "true"
    const rect = isAnchor || noIntersectionObserver ? child.getBoundingClientRect() : null
    const isVisible = noIntersectionObserver && rect
      ? rect.bottom > anchorLine && rect.top < viewportRect.bottom
      : visibleMessageIds.has(messageId)
    if (isVisible) visible.push(messageId)
    if (isAnchor && rect && rect.top <= anchorLine + SCROLL_EPSILON) currentAnchorId = messageId
  }

  return visible.length === 0 && currentAnchorId === null
    ? EMPTY_VISIBILITY
    : { currentAnchorId, visibleMessageIds: visible }
}

function findFirstAnchorFrom(elements: HTMLElement[], startIndex: number): HTMLElement | null {
  for (let i = startIndex; i < elements.length; i++) {
    if (elements[i]?.dataset.scrollAnchor === "true") return elements[i]!
  }
  return null
}

function findFirstUnhandledAnchor(elements: HTMLElement[], handled: WeakSet<HTMLElement>): HTMLElement | null {
  for (const element of elements) {
    if (element.dataset.scrollAnchor === "true" && !handled.has(element)) return element
  }
  return null
}

function hasMultipleAnchorsFrom(elements: HTMLElement[], startIndex: number): boolean {
  let count = 0
  for (let i = startIndex; i < elements.length; i++) {
    if (elements[i]?.dataset.scrollAnchor === "true") {
      count += 1
      if (count > 1) return true
    }
  }
  return false
}

function findLastAnchor(elements: HTMLElement[]): HTMLElement | null {
  for (let i = elements.length - 1; i >= 0; i--) {
    if (elements[i]?.dataset.scrollAnchor === "true") return elements[i]!
  }
  return null
}

function findFirstVisibleMessage({
  content,
  spacer,
  viewport,
}: {
  content: HTMLElement
  spacer: HTMLElement | null
  viewport: HTMLElement
}): HTMLElement | null {
  const viewportRect = viewport.getBoundingClientRect()
  for (const child of getMessageChildren(content, spacer)) {
    if (!child.dataset.messageId) continue
    const rect = child.getBoundingClientRect()
    if (rect.bottom > viewportRect.top && rect.top < viewportRect.bottom) return child
  }
  return null
}

// ---------------------------------------------------------------------------
// Engine
// ---------------------------------------------------------------------------

export interface MessageScrollerContext {
  autoscrolling: Readonly<ShallowRef<boolean>>
  scrollable: Readonly<ShallowRef<MessageScrollerScrollable>>
  scrollableAttr: ComputedRef<string | undefined>
  visibility: Readonly<ShallowRef<MessageScrollerVisibilityState>>
  acquireVisibility: () => void
  releaseVisibility: () => void
  handleContentChange: () => void
  handleResize: () => void
  scrollToEnd: (options?: { behavior?: ScrollBehavior }) => boolean
  scrollToMessage: (messageId: string, options?: MessageScrollerScrollOptions) => boolean
  scrollToStart: (options?: { behavior?: ScrollBehavior }) => boolean
  setContentElement: (element: HTMLElement | null) => void
  setSpacerElement: (element: HTMLElement | null) => void
  setViewportElement: (element: HTMLElement | null) => void
  setPreserveScrollOnPrepend: (value: boolean) => void
  syncAfterScroll: () => void
  userScrollIntent: () => void
}

export type RegisterMessage = (
  messageId: string,
  element: HTMLElement | null,
  previousElement: HTMLElement | null,
) => void

const CONTEXT_KEY: InjectionKey<MessageScrollerContext> = Symbol("MessageScrollerContext")
const REGISTER_KEY: InjectionKey<RegisterMessage> = Symbol("MessageScrollerRegister")

function createEngine(props: MessageScrollerProviderProps) {
  const autoScroll = () => props.autoScroll ?? false
  const defaultScrollPosition = () => props.defaultScrollPosition ?? "end"
  const scrollEdgeThreshold = () => props.scrollEdgeThreshold ?? DEFAULT_SCROLL_EDGE_THRESHOLD
  const scrollPreviousItemPeek = () => props.scrollPreviousItemPeek ?? DEFAULT_SCROLL_PREVIOUS_ITEM_PEEK
  const scrollMargin = () => props.scrollMargin ?? DEFAULT_SCROLL_MARGIN

  let viewport: HTMLElement | null = null
  let content: HTMLElement | null = null
  let spacer: HTMLElement | null = null
  let spacerGap = 0
  let spacerHeight = 0
  let mode: Mode = autoScroll() ? "following-bottom" : "free-scrolling"
  /**
   * True from the moment the reader drives the viewport until something explicitly asks for the
   * live edge again. Wheel, touch, key and native scrollbar drag all mean the same thing;
   * `autoscrolling` only covers "the engine is animating", which is a different claim.
   */
  let userDriven = false
  /**
   * Where the engine last asked the viewport to go, or null when it is not scrolling. This is how a
   * native scrollbar drag is told apart from the engine's own animation: both arrive as the same
   * `scroll` event, so the only thing that can distinguish them is whether the movement is closing
   * in on a target the engine set. Cleared on arrival, so the next movement is judged afresh.
   */
  let engineTarget: number | null = null
  let streamingTurn: HTMLElement | null = null
  let firstItem: HTMLElement | null = null
  let itemCount = 0
  let lastScrollTop = 0
  let defaultScrollPositionApplied = false
  let preserveScrollOnPrepend = true
  let prependRestore: PrependRestore | null = null
  let pendingScrollToMessage: PendingScrollToMessage | null = null
  let stateFrame: number | null = null
  let visibilityFrame: number | null = null
  let pendingScrollFrame: number | null = null
  let autoscrollingTimeout: number | null = null
  let visibilityObserver: IntersectionObserver | null = null
  let visibilityConsumers = 0
  const messageElements = new Map<string, HTMLElement>()
  const visibleMessageIds = new Set<string>()
  const handledScrollAnchors = new WeakSet<HTMLElement>()

  const autoscrolling = shallowRef(false)
  const scrollable = shallowRef<MessageScrollerScrollable>(EMPTY_SCROLLABLE)
  const visibility = shallowRef<MessageScrollerVisibilityState>(EMPTY_VISIBILITY)
  const scrollableAttr = computed(() => {
    const attr = [scrollable.value.start && "start", scrollable.value.end && "end"].filter(Boolean).join(" ")
    return attr || undefined
  })

  // --- scroll-state commit / visibility scheduling ---------------------------

  function updateModeFromScroll(next: MessageScrollerScrollable) {
    const scrollTop = viewport?.scrollTop ?? 0
    const scrolledUp = scrollTop < lastScrollTop - SCROLL_EPSILON
    // Credit the movement before reading it, because a reader's drag and the engine's own
    // animation both arrive here as the same `scroll` event. See `scrollIntent.ts`.
    if (classifyScroll({ scrollTop, previousScrollTop: lastScrollTop, engineTarget }) === "reader") userDriven = true
    if (engineTarget !== null && Math.abs(engineTarget - scrollTop) <= SCROLL_EPSILON) engineTarget = null
    lastScrollTop = scrollTop
    // Whether the engine may take the live edge back. A jump to a message and the settle that
    // follows it own the viewport until they finish.
    const canFollow = autoScroll() && mode !== "settling-jump" && mode !== "anchored-to-message"

    // The reader's intent outranks the default, so it is decided first. Checking the follow case
    // ahead of it (as this did) short-circuits on exactly the state that matters: a drag that stops
    // within one edge threshold of the bottom reads as `end: false`, so it took the follow branch,
    // found `userDriven` set, changed nothing, and never reached the branch below that hands
    // control back — the viewport stayed stuck following while the reader was holding it.
    if (userDriven) {
      // Arriving at the live edge by hand hands control back to the engine, which is what lets a
      // later streamed chunk follow again. Gated on not scrolling up so the small upward nudge
      // that begins a gesture near the bottom is not mistaken for arriving there.
      if (canFollow && !next.end && !scrolledUp) {
        userDriven = false
        mode = "following-bottom"
      } else if (canFollow || mode === "following-bottom") {
        mode = "free-scrolling"
      }
      return
    }

    if (canFollow && !next.end) {
      mode = "following-bottom"
    } else if (mode === "following-bottom" && next.end && scrolledUp && !autoscrolling.value) {
      mode = "free-scrolling"
    }
  }

  function commitScrollState() {
    const measured = computeScrollable({ content, scrollEdgeThreshold: scrollEdgeThreshold(), spacer, viewport })
    updateModeFromScroll(measured)
    const next = mode === "following-bottom" ? { ...measured, end: false } : measured
    if (!scrollableEqual(scrollable.value, next)) scrollable.value = next
  }

  function scheduleStateCommit() {
    if (stateFrame === null) {
      stateFrame = window.requestAnimationFrame(() => {
        stateFrame = null
        commitScrollState()
      })
    }
  }

  function scheduleVisibilitySync() {
    if (visibilityConsumers === 0) return
    if (visibilityFrame === null) {
      visibilityFrame = window.requestAnimationFrame(() => {
        visibilityFrame = null
        if (visibilityConsumers > 0) {
          const next = computeVisibility({
            content,
            scrollMargin: scrollMargin(),
            scrollPreviousItemPeek: scrollPreviousItemPeek(),
            spacer,
            viewport,
            visibleMessageIds,
          })
          if (!visibilityEqual(visibility.value, next)) visibility.value = next
        }
      })
    }
  }

  // --- imperative scroll primitives ------------------------------------------

  function setAutoscrolling(active: boolean) {
    if (autoscrollingTimeout !== null) {
      window.clearTimeout(autoscrollingTimeout)
      autoscrollingTimeout = null
    }
    if (autoscrolling.value !== active) {
      autoscrolling.value = active
      commitScrollState()
    }
    if (active) {
      autoscrollingTimeout = window.setTimeout(() => {
        autoscrollingTimeout = null
        autoscrolling.value = false
        commitScrollState()
      }, AUTOSCROLLING_TIMEOUT)
    }
  }

  function setSpacerHeight(height: number) {
    if (!spacer) return
    const next = Math.max(0, Math.ceil(height))
    if (spacerHeight !== next) {
      spacerHeight = next
      spacer.hidden = next === 0
      spacer.style.height = `${next}px`
      spacer.style.marginTop = next > 0 ? `${-spacerGap}px` : ""
    }
  }

  function scrollTo(
    top: number,
    { behavior = "auto", autoscrolling: isAutoscrolling = false }: { behavior?: ScrollBehavior; autoscrolling?: boolean } = {},
  ) {
    if (!viewport) return
    const target = Math.max(0, top)
    if (Math.abs(viewport.scrollTop - target) <= SCROLL_EPSILON) {
      viewport.scrollTop = target
      engineTarget = null
      commitScrollState()
      return
    }
    if (isAutoscrolling) setAutoscrolling(true)
    // Record the destination before the browser starts moving, because every `scroll` event the
    // animation emits is checked against it (including the frames after `scrollTo` has returned).
    engineTarget = target
    viewport.scrollTo({ top: target, behavior })
    scheduleStateCommit()
  }

  function scrollToStart({ behavior = "auto" }: { behavior?: ScrollBehavior } = {}): boolean {
    if (!viewport) return false
    userDriven = true
    setSpacerHeight(0)
    streamingTurn = null
    mode = "free-scrolling"
    scrollTo(0, { behavior })
    scheduleVisibilitySync()
    return true
  }

  function scrollToEnd({ behavior = "auto" }: { behavior?: ScrollBehavior } = {}): boolean {
    if (!viewport) return false
    // Asking for the bottom is the reader (or a caller acting for them) re-entering the live edge,
    // so following may resume. Anything else keeps their position authoritative.
    userDriven = false
    setSpacerHeight(0)
    streamingTurn = null
    mode = autoScroll() ? "following-bottom" : "free-scrolling"
    scrollTo(maxScrollTop(viewport), { autoscrolling: true, behavior })
    scheduleVisibilitySync()
    return true
  }

  function scrollToElement(
    element: HTMLElement,
    {
      align = "start",
      behavior = "auto",
      scrollMargin: margin = scrollMargin(),
    }: MessageScrollerScrollOptions = {},
    { keepPreviousPeek = false }: { keepPreviousPeek?: boolean } = {},
  ): boolean {
    if (!content || !viewport || !content.contains(element)) return false
    const targetScrollTop = computeScrollTopForElement({
      align,
      element,
      scrollMargin: keepPreviousPeek ? margin + scrollPreviousItemPeek() : margin,
      spacer,
      viewport,
    })
    setSpacerHeight(computeSpacerHeight({ content, scrollTop: targetScrollTop, spacer, viewport }))
    prependRestore = { element, viewportTop: getRelativeTop(element, viewport) }
    mode = keepPreviousPeek ? "anchored-to-message" : "settling-jump"
    streamingTurn = keepPreviousPeek ? element : null
    scrollTo(targetScrollTop, { behavior })
    scheduleVisibilitySync()
    return true
  }

  function reanchorToAnchoredMessage(): boolean {
    if (!streamingTurn || !streamingTurn.isConnected || mode !== "anchored-to-message") return false
    return scrollToElement(streamingTurn, { align: "start" }, { keepPreviousPeek: true })
  }

  function scrollToMessage(messageId: string, options?: MessageScrollerScrollOptions): boolean {
    const element = messageElements.get(messageId)
    if (element) {
      defaultScrollPositionApplied = true
      if (scrollToElement(element, options)) {
        pendingScrollToMessage = null
        return true
      }
      pendingScrollToMessage = { messageId, options }
      return true
    }
    if (itemCount === 0) {
      pendingScrollToMessage = { messageId, options }
      defaultScrollPositionApplied = true
      return true
    }
    return false
  }

  function flushPendingScrollToMessage(): boolean {
    if (!pendingScrollToMessage) return false
    const element = messageElements.get(pendingScrollToMessage.messageId)
    if (!element || !scrollToElement(element, pendingScrollToMessage.options)) return false
    pendingScrollToMessage = null
    defaultScrollPositionApplied = true
    return true
  }

  // --- prepend preservation --------------------------------------------------

  function applyPrependRestore(): boolean {
    if (!prependRestore || !viewport || !prependRestore.element.isConnected) return false
    const delta = getRelativeTop(prependRestore.element, viewport) - prependRestore.viewportTop
    if (Math.abs(delta) <= SCROLL_EPSILON) return false
    // Holding the reader's place while history is inserted above is the engine moving the
    // viewport, even though the delta comes from the layout rather than from a target. Claim the
    // movement so the resulting `scroll` event is not read as a drag.
    engineTarget = viewport.scrollTop + delta
    viewport.scrollTop += delta
    prependRestore.viewportTop = getRelativeTop(prependRestore.element, viewport)
    scheduleStateCommit()
    scheduleVisibilitySync()
    return true
  }

  function capturePrependAnchor() {
    if (!content || !viewport) {
      prependRestore = null
      return
    }
    const element = findFirstVisibleMessage({ content, spacer, viewport })
    prependRestore = element ? { element, viewportTop: getRelativeTop(element, viewport) } : null
  }

  function schedulePrependFlush() {
    if (pendingScrollFrame === null) {
      pendingScrollFrame = window.requestAnimationFrame(() => {
        pendingScrollFrame = null
        if (flushPendingScrollToMessage()) capturePrependAnchor()
      })
    }
  }

  // --- default scroll position -----------------------------------------------

  function applyDefaultScrollPosition(): boolean {
    if (defaultScrollPositionApplied || itemCount === 0) return false
    const position = defaultScrollPosition()
    let applied = false
    if (position === "last-anchor") {
      const lastAnchor = content && viewport ? findLastAnchor(getMessageChildren(content, spacer)) : null
      if (!content || !viewport || !lastAnchor) {
        applied = scrollToEnd({ behavior: "auto" })
      } else {
        const anchorOffset = getElementOffsetTop(lastAnchor, viewport)
        const contentHeight = measureContentHeight({ content, spacer, viewport })
        applied = contentHeight - anchorOffset <= viewport.clientHeight
          ? scrollToEnd({ behavior: "auto" })
          : scrollToElement(lastAnchor, { align: "start" }, { keepPreviousPeek: true })
      }
    } else {
      applied = position === "end" ? scrollToEnd({ behavior: "auto" }) : scrollToStart({ behavior: "auto" })
    }
    if (applied) {
      defaultScrollPositionApplied = true
      return true
    }
    return false
  }

  // --- content / resize handling ---------------------------------------------

  function applyContentChange(children: HTMLElement[], previousCount: number, previousFirst: HTMLElement | null) {
    if (flushPendingScrollToMessage()) return
    if (previousCount === 0) {
      // Every anchor present at first paint is part of the initial state, not a new turn. Leaving
      // them unmarked made the `children.length === previousCount` branch below treat the earliest
      // one as pending on the next same-count mutation (a streaming reply replaced by its
      // persisted message, a live turn swapped in) and scroll the reader to the top of the thread.
      for (const child of children) {
        if (child.dataset.scrollAnchor === "true") handledScrollAnchors.add(child)
      }
      if (applyDefaultScrollPosition() || (children.length > 0 && autoScroll() && scrollToEnd({ behavior: "auto" }))) {
        return
      }
      commitScrollState()
      scheduleVisibilitySync()
      return
    }
    const previousIndex = previousFirst ? children.indexOf(previousFirst) : -1
    if (preserveScrollOnPrepend && previousIndex > 0) {
      applyPrependRestore()
      return
    }
    if (children.length > previousCount) {
      const anchor = findFirstAnchorFrom(children, previousCount)
      if (anchor) {
        if (autoScroll() && mode === "following-bottom" && hasMultipleAnchorsFrom(children, previousCount)) {
          scrollToEnd({ behavior: "auto" })
          return
        }
        scrollToElement(anchor, { align: "start" }, { keepPreviousPeek: true })
        handledScrollAnchors.add(anchor)
        return
      }
    }
    if (children.length === previousCount) {
      const anchor = findFirstUnhandledAnchor(children, handledScrollAnchors)
      if (anchor) {
        scrollToElement(anchor, { align: "start" }, { keepPreviousPeek: true })
        handledScrollAnchors.add(anchor)
        return
      }
    }
    if (mode === "following-bottom" && autoScroll()) {
      scrollToEnd({ behavior: "auto" })
    } else {
      commitScrollState()
      scheduleVisibilitySync()
    }
  }

  function handleContentChange() {
    if (!content) return
    const children = getMessageChildren(content, spacer)
    const previousCount = itemCount
    const previousFirst = firstItem
    itemCount = children.length
    firstItem = children[0] ?? null

    applyContentChange(children, previousCount, previousFirst)
    capturePrependAnchor()
  }

  function handleResize() {
    if (mode === "following-bottom" && autoScroll()) {
      scrollToEnd({ behavior: "auto" })
      return
    }
    const previousSpacerHeight = spacerHeight
    if (reanchorToAnchoredMessage()) {
      // The reply streaming below the anchor consumes the tail spacer as it grows. Once the
      // last of it is gone the reply has filled the viewport and the reader is genuinely at
      // the live edge, so autoScroll hands off from the anchor hold to following the bottom.
      // Requiring the >0 → 0 transition keeps a turn taller than the viewport (placed with no
      // spacer) held instead of yanked to the end.
      if (autoScroll() && previousSpacerHeight > 0 && spacerHeight === 0) scrollToEnd({ behavior: "auto" })
      return
    }
    scheduleStateCommit()
    scheduleVisibilitySync()
  }

  // --- visibility observation ------------------------------------------------

  function observeVisibility() {
    if (!viewport || visibilityConsumers === 0) return
    if (typeof IntersectionObserver === "undefined") {
      scheduleVisibilitySync()
      return
    }
    if (!visibilityObserver) {
      visibilityObserver = new IntersectionObserver(
        (entries) => {
          for (const entry of entries) {
            const messageId = (entry.target as HTMLElement).dataset.messageId
            if (!messageId) continue
            if (entry.isIntersecting) visibleMessageIds.add(messageId)
            else visibleMessageIds.delete(messageId)
          }
          scheduleVisibilitySync()
        },
        {
          root: viewport,
          rootMargin: `${-(scrollMargin() + scrollPreviousItemPeek())}px 0px 0px 0px`,
          threshold: [0, 0.01, 0.5, 1],
        },
      )
    }
    messageElements.forEach((element) => {
      visibilityObserver?.observe(element)
    })
    scheduleVisibilitySync()
  }

  function unobserveVisibility() {
    if (visibilityFrame !== null) {
      window.cancelAnimationFrame(visibilityFrame)
      visibilityFrame = null
    }
    visibilityObserver?.disconnect()
    visibilityObserver = null
    visibleMessageIds.clear()
    if (!visibilityEqual(visibility.value, EMPTY_VISIBILITY)) visibility.value = EMPTY_VISIBILITY
  }

  function acquireVisibility() {
    visibilityConsumers += 1
    if (visibilityConsumers === 1) observeVisibility()
  }

  function releaseVisibility() {
    visibilityConsumers -= 1
    if (visibilityConsumers === 0) unobserveVisibility()
  }

  const registerMessage: RegisterMessage = (messageId, element, previousElement) => {
    if (element) {
      messageElements.set(messageId, element)
      visibilityObserver?.observe(element)
      scheduleVisibilitySync()
      if (pendingScrollToMessage?.messageId === messageId) schedulePrependFlush()
      return
    }
    if (previousElement && messageElements.get(messageId) === previousElement) {
      messageElements.delete(messageId)
      visibleMessageIds.delete(messageId)
      visibilityObserver?.unobserve(previousElement)
      scheduleVisibilitySync()
    }
  }

  // --- user intent + element setters -----------------------------------------

  function userScrollIntent() {
    userDriven = true
    if (mode === "following-bottom" || mode === "anchored-to-message" || mode === "settling-jump") {
      streamingTurn = null
      mode = "free-scrolling"
    }
  }

  function setViewportElement(element: HTMLElement | null) {
    viewport = element
    // A visibility consumer may have subscribed before the viewport mounted, in which case
    // observeVisibility() bailed out. Retry now it exists.
    if (element) observeVisibility()
  }

  function setContentElement(element: HTMLElement | null) {
    content = element
  }

  function setSpacerElement(element: HTMLElement | null) {
    spacer = element
    spacerGap = getRowGap(element?.parentElement ?? null)
  }

  function setPreserveScrollOnPrepend(value: boolean) {
    preserveScrollOnPrepend = value
  }

  function syncAfterScroll() {
    commitScrollState()
    scheduleVisibilitySync()
    capturePrependAnchor()
  }

  function onAutoScrollChange() {
    if (autoScroll() && mode === "following-bottom" && itemCount > 0) {
      scrollToEnd({ behavior: "auto" })
      return
    }
    commitScrollState()
  }

  function destroy() {
    if (stateFrame !== null) {
      window.cancelAnimationFrame(stateFrame)
      stateFrame = null
    }
    if (visibilityFrame !== null) {
      window.cancelAnimationFrame(visibilityFrame)
      visibilityFrame = null
    }
    if (autoscrollingTimeout !== null) {
      window.clearTimeout(autoscrollingTimeout)
      autoscrollingTimeout = null
    }
    if (pendingScrollFrame !== null) {
      window.cancelAnimationFrame(pendingScrollFrame)
      pendingScrollFrame = null
    }
    visibilityObserver?.disconnect()
    visibilityObserver = null
  }

  const context: MessageScrollerContext = {
    autoscrolling,
    scrollable,
    scrollableAttr,
    visibility,
    acquireVisibility,
    releaseVisibility,
    handleContentChange,
    handleResize,
    scrollToEnd,
    scrollToMessage,
    scrollToStart,
    setContentElement,
    setSpacerElement,
    setViewportElement,
    setPreserveScrollOnPrepend,
    syncAfterScroll,
    userScrollIntent,
  }

  return { context, registerMessage, applyDefaultScrollPosition, onAutoScrollChange, destroy }
}

// ---------------------------------------------------------------------------
// Provider wiring (provide/inject)
// ---------------------------------------------------------------------------

export function provideMessageScroller(props: MessageScrollerProviderProps) {
  const engine = createEngine(props)
  provide(CONTEXT_KEY, engine.context)
  provide(REGISTER_KEY, engine.registerMessage)

  watch(() => props.autoScroll ?? false, () => engine.onAutoScrollChange())

  onMounted(() => {
    engine.applyDefaultScrollPosition()
    // The viewport element attaches in the viewport component's onMounted, after the
    // content component ran its initial handleContentChange without it. Re-sync now that
    // every element is wired up.
    engine.context.syncAfterScroll()
  })

  onScopeDispose(() => engine.destroy())

  return engine
}

export function useMessageScrollerContext(): MessageScrollerContext {
  const context = inject(CONTEXT_KEY, null)
  if (!context) throw new Error("useMessageScroller must be used within a MessageScroller.")
  return context
}

export function useMessageScrollerRegister(): RegisterMessage {
  const register = inject(REGISTER_KEY, null)
  if (!register) throw new Error("MessageScrollerItem must be used within a MessageScroller.")
  return register
}

export function useMessageScroller() {
  const { scrollToEnd, scrollToMessage, scrollToStart } = useMessageScrollerContext()
  return { scrollToEnd, scrollToMessage, scrollToStart }
}

export function useMessageScrollerScrollable(): Ref<MessageScrollerScrollable> {
  const { scrollable } = useMessageScrollerContext()
  return computed(() => scrollable.value)
}

export function useMessageScrollerVisibility(): Ref<MessageScrollerVisibilityState> {
  const { acquireVisibility, releaseVisibility, visibility } = useMessageScrollerContext()
  acquireVisibility()
  if (getCurrentScope()) onScopeDispose(releaseVisibility)
  return computed(() => visibility.value)
}
