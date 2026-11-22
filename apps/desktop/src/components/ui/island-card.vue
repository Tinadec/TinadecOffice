<script setup lang="ts">
type Variant = 'section' | 'raised'

withDefaults(defineProps<{
  variant?: Variant
  padding?: 'none' | 'sm' | 'md'
  hoverable?: boolean
  divided?: boolean
}>(), {
  variant: 'section',
  padding: 'md',
  hoverable: false,
  divided: false,
})
</script>

<template>
  <article
    class="island-card"
    :class="[`variant-${variant}`, `pad-${padding}`, { hoverable, divided }]"
  >
    <header v-if="$slots.header" class="island-header">
      <slot name="header" />
    </header>
    <div class="island-body">
      <slot />
    </div>
    <footer v-if="$slots.footer" class="island-footer">
      <slot name="footer" />
    </footer>
  </article>
</template>

<style scoped>
/* Consumes inherited --surface-* tokens only; never a material root itself. */
.island-card {
  --island-pad: 12px;
  background: var(--surface-raised);
  border: 1px solid var(--border-card, var(--border-default));
  border-radius: var(--radius-island, 12px);
  box-shadow: var(--shadow-card-subtle);
  overflow: hidden;
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  transition: box-shadow 180ms var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
              border-color 180ms var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
              background-color 180ms var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}

.island-card.variant-section {
  background: var(--surface-section);
}

.island-card.hoverable:hover {
  box-shadow: var(--shadow-card-hover);
  border-color: var(--border-card-active, var(--border-default));
}

.island-card.pad-sm { --island-pad: 8px; }
.island-card.pad-none { --island-pad: 0px; }

.island-header {
  padding: var(--island-pad) var(--island-pad) calc(var(--island-pad) * 0.5);
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

.island-body {
  flex: 1;
  min-height: 0;
  min-width: 0;
  padding: var(--island-pad);
}

.island-header + .island-body {
  padding-top: calc(var(--island-pad) * 0.5);
}

.island-footer {
  padding: calc(var(--island-pad) * 0.5) var(--island-pad) var(--island-pad);
  flex-shrink: 0;
}

.island-card.divided .island-header {
  padding-bottom: calc(var(--island-pad) * 0.75);
  border-bottom: 1px solid var(--border-muted, var(--border-default));
}

.island-card.divided .island-header + .island-body {
  padding-top: var(--island-pad);
}

.island-card.divided .island-footer {
  padding-top: calc(var(--island-pad) * 0.75);
  border-top: 1px solid var(--border-muted, var(--border-default));
}

@media (prefers-reduced-motion: reduce) {
  .island-card {
    transition: none;
  }
}
</style>
