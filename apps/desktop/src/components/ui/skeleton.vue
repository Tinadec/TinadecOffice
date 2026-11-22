<script setup lang="ts">
import { cn } from '@/lib/utils'

interface Props {
  class?: string
}

const props = defineProps<Props>()
</script>

<template vapor>
  <div
    data-slot="skeleton"
    aria-hidden="true"
    :class="cn('ui-skeleton rounded-[var(--radius-control,6px)]', props.class)"
  />
</template>

<style scoped>
@keyframes ui-skeleton-shimmer {
  0% {
    background-position: 200% 0;
  }
  100% {
    background-position: -200% 0;
  }
}

.ui-skeleton {
  background-color: color-mix(in srgb, var(--text-primary) 7%, transparent);
  background-image: linear-gradient(
    90deg,
    transparent 0%,
    color-mix(in srgb, var(--text-primary) 7%, transparent) 45%,
    color-mix(in srgb, var(--text-primary) 13%, transparent) 50%,
    color-mix(in srgb, var(--text-primary) 7%, transparent) 55%,
    transparent 100%
  );
  background-size: 200% 100%;
  animation: ui-skeleton-shimmer 1.8s ease-in-out infinite;
}

@media (prefers-reduced-motion: reduce) {
  .ui-skeleton {
    animation: none;
    background-image: none;
  }
}
</style>
