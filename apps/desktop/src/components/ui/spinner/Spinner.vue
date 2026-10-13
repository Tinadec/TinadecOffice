<script setup lang="ts">
import type { HTMLAttributes } from "vue"
import { Loader2Icon } from "@lucide/vue"
import { cn } from "@/lib/utils"

const props = defineProps<{
  class?: HTMLAttributes["class"]
  /** Accessible name; the spinner is a status region. */
  label?: string
  /**
   * Inside a region that already announces its state (a polite list, a labelled button),
   * a second status would be read twice; a decorative spinner is hidden from assistive tech.
   */
  decorative?: boolean
}>()
</script>

<template>
  <!-- Reduced motion swaps the spin for a gentle pulse rather than freezing the glyph. -->
  <Loader2Icon
    data-slot="spinner"
    :role="decorative ? undefined : 'status'"
    :aria-label="decorative ? undefined : (label ?? 'Loading')"
    :aria-hidden="decorative ? 'true' : undefined"
    :class="cn('size-3.5 animate-spin motion-reduce:animate-pulse', props.class)"
  />
</template>
