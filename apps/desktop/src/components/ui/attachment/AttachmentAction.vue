<script setup lang="ts">
import type { HTMLAttributes } from "vue"
import { cn } from "@/lib/utils"

// Upstream wraps the registry Button (`icon-xs`); this app's UiButton has no icon-xs size,
// so the action is a plain button with the same footprint.
const props = defineProps<{
  class?: HTMLAttributes["class"]
}>()
</script>

<template>
  <button
    type="button"
    data-slot="attachment-action"
    :class="cn('cn-attachment-action inline-flex size-6 items-center justify-center rounded-md text-[var(--text-secondary)] transition-colors hover:bg-[var(--surface-hover)] hover:text-[var(--text-primary)] focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring [&_svg]:size-3.5', props.class)"
  >
    <slot />
  </button>
</template>
