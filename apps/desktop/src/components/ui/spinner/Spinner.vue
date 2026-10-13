<script setup lang="ts">
import type { HTMLAttributes } from "vue"
import { Loader2Icon } from "@lucide/vue"
import { cn } from "@/lib/utils"

const props = defineProps<{
  class?: HTMLAttributes["class"]
  /** Accessible name; the spinner is a status region. */
  label?: string
}>()
</script>

<template>
  <!-- Reduced motion swaps the spin for a gentle pulse rather than freezing the glyph. -->
  <Loader2Icon
    data-slot="spinner"
    role="status"
    :aria-label="label ?? 'Loading'"
    :class="cn('size-3.5 animate-spin motion-reduce:animate-pulse', props.class)"
  />
</template>
