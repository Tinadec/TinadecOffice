<script setup lang="ts">
import type { HTMLAttributes } from "vue"
import type { MessageScrollerButtonDirection } from "./useMessageScroller"
import { ArrowDownIcon } from "@lucide/vue"
import { computed } from "vue"
import { cn } from "@/lib/utils"
import { useMessageScroller, useMessageScrollerScrollable } from "./useMessageScroller"

const props = withDefaults(defineProps<{
  class?: HTMLAttributes["class"]
  direction?: MessageScrollerButtonDirection
  behavior?: ScrollBehavior
  /** Accessible name; the default glyph has no text of its own. */
  label?: string
}>(), {
  direction: "end",
  behavior: "smooth",
})

const { scrollToEnd, scrollToStart } = useMessageScroller()
const scrollable = useMessageScrollerScrollable()

const active = computed(() =>
  props.direction === "start" ? scrollable.value.start : scrollable.value.end)

function onClick(event: MouseEvent) {
  if (!active.value)
    return
  const target = event.currentTarget as HTMLElement | null
  target?.blur()
  if (event.defaultPrevented)
    return
  if (props.direction === "start")
    scrollToStart({ behavior: props.behavior })
  else
    scrollToEnd({ behavior: props.behavior })
}
</script>

<template>
  <!-- Upstream wraps the registry Button (icon-sm); this app's UiButton has no such size. -->
  <button
    type="button"
    data-slot="message-scroller-button"
    :data-direction="direction"
    :data-active="active ? 'true' : 'false'"
    :inert="!active"
    :tabindex="active ? undefined : -1"
    :class="cn(
      'absolute inset-s-1/2 inline-flex size-8 -translate-x-1/2 items-center justify-center gap-1 rounded-full border border-[var(--border-card)] bg-[var(--surface-raised)] text-[var(--text-primary)] transition-[translate,scale,opacity] duration-200 hover:bg-[var(--surface-hover)] focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring [&_svg]:size-3.5 data-[active=false]:pointer-events-none data-[active=false]:scale-95 data-[active=false]:opacity-0 data-[active=false]:duration-400 data-[active=false]:ease-[cubic-bezier(0.7,0,0.84,0)] data-[active=true]:translate-y-0 data-[active=true]:scale-100 data-[active=true]:opacity-100 data-[active=true]:ease-[cubic-bezier(0.23,1,0.32,1)] data-[direction=end]:bottom-4 data-[direction=end]:data-[active=false]:translate-y-full data-[direction=start]:top-4 data-[direction=start]:data-[active=false]:-translate-y-full rtl:translate-x-1/2 data-[direction=start]:[&_svg]:rotate-180 motion-reduce:transition-none',
      props.class,
    )"
    @click="onClick"
  >
    <slot>
      <ArrowDownIcon />
      <span class="sr-only">{{ label ?? (direction === "end" ? "Scroll to end" : "Scroll to start") }}</span>
    </slot>
  </button>
</template>
