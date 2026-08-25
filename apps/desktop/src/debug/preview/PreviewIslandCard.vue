<script setup lang="ts">
import { computed } from 'vue'
import { usePanelStyles } from '@/composables/usePanelStyles'

type Variant = 'section' | 'raised'

const props = withDefaults(defineProps<{
  variant?: Variant
  hoverable?: boolean
  selected?: boolean
  draggable?: boolean
  padding?: 'none' | 'sm' | 'md'
  immersive?: boolean
}>(), {
  variant: 'raised',
  hoverable: true,
  selected: false,
  draggable: false,
  padding: 'md',
  immersive: false,
})

const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()

const materialStyle = computed(() => {
  if (props.immersive) {
    const s = getPanelStyle()
    const { backgroundColor, backdropFilter, WebkitBackdropFilter, ...rest } = s as Record<string, string>
    return rest
  }
  // section / raised 均跟随全局材质，但保留阴影/圆角由组件自身提供
  return getPanelStyle()
})
const materialAttrs = computed(() => getPanelDataAttributes())
</script>

<template>
  <article
    class="preview-island"
    :class="[
      `variant-${variant}`,
      `pad-${padding}`,
      { hoverable, selected, draggable, immersive }
    ]"
    v-bind="materialAttrs"
    :style="materialStyle"
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
.preview-island {
  background: var(--surface-raised);
  border: 1px solid var(--border-card);
  border-radius: 12px;
  box-shadow: var(--shadow-card-subtle);
  overflow: hidden;
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  transition:
    box-shadow 0.2s ease,
    transform 0.2s ease,
    border-color 0.2s ease,
    background-color 0.2s ease;
}

.preview-island.variant-section {
  background: var(--surface-section);
}

/* immersive: 完全透明容器，仅透出背景层，保留 data-panel-effect 供内部继承 */
.preview-island.immersive {
  background: transparent !important;
  border: none !important;
  box-shadow: none !important;
  border-radius: 0 !important;
}

.preview-island.hoverable:not(.immersive):hover {
  box-shadow: var(--shadow-card-hover);
  transform: translateY(-1px);
  border-color: var(--border-card-active);
}

.preview-island.selected:not(.immersive) {
  border-color: var(--accent-primary, #2ec4b6);
  box-shadow:
    0 0 0 1px var(--accent-primary, #2ec4b6),
    var(--shadow-card-subtle);
}

.preview-island.draggable {
  cursor: grab;
}
.preview-island.draggable:active {
  cursor: grabbing;
  transform: translateY(-2px) scale(1.01);
  box-shadow:
    0 4px 12px rgba(0, 0, 0, 0.14),
    0 12px 32px rgba(0, 0, 0, 0.1);
  opacity: 0.96;
}

.island-header {
  padding: 10px 14px 9px;
  border-bottom: 1px solid var(--border-default, rgba(255, 255, 255, 0.08));
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary, #e6edf3);
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

.island-body {
  flex: 1;
  min-height: 0;
  min-width: 0;
}

.preview-island.pad-none .island-body { padding: 0; }
.preview-island.pad-sm .island-body { padding: 10px; }
.preview-island.pad-md .island-body { padding: 16px; }

.preview-island.pad-none .island-header { padding: 10px 14px 9px; }
.preview-island.pad-sm .island-header { padding: 8px 10px 7px; }

.island-footer {
  padding: 8px 14px;
  border-top: 1px solid var(--border-default, rgba(255, 255, 255, 0.08));
  flex-shrink: 0;
}

@media (prefers-reduced-motion: reduce) {
  .preview-island,
  .preview-island.hoverable:hover,
  .preview-island.draggable:active {
    transition: none;
    transform: none;
  }
}
</style>
