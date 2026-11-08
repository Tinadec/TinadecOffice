<script setup lang="ts">
import { Layers, PawPrint } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAgentAvatars } from '@/composables/useAgentAvatars'
import { planAvatar, type AvatarKind } from '@/pets/avatarKind'

/**
 * One agent's face.
 *
 * The sprite sheet is an 8-column grid of 192x208 frames; showing frame (0,0) is a matter of
 * making the image eight times wider than the slot and letting `overflow: hidden` do the crop —
 * the same trick `PetPreview` uses at card scale, without its lazy-loading machinery.
 *
 * Two ways to bind:
 * - `instance-key` (+ `agent-id`): the face of one spawned agent, resolved and persisted once.
 * - `pet-slug`: a literal pet, for settings previews and pinned choices.
 *
 * A missing image is never an empty hole: it falls back to a glyph, and the URL layer already
 * degrades a deleted pet to the bundled one.
 */
const props = withDefaults(defineProps<{
  instanceKey?: string
  agentId?: string | null
  petSlug?: string
  /** Which renderer this avatar needs; sprite is the only one this build can draw. */
  avatarKind?: AvatarKind
  /** Width in pixels; the height follows the 192:208 frame. */
  size?: number
  alt?: string
}>(), {
  instanceKey: '',
  agentId: null,
  petSlug: '',
  avatarKind: 'sprite',
  size: 20,
  alt: '',
})

const { t } = useI18n()
const { urlForSlug, resolveInstanceAvatar, revision } = useAgentAvatars()
const resolvedSlug = ref(props.petSlug)

async function sync() {
  if (props.petSlug) {
    resolvedSlug.value = props.petSlug
    return
  }
  if (!props.instanceKey) {
    resolvedSlug.value = ''
    return
  }
  resolvedSlug.value = await resolveInstanceAvatar(props.instanceKey, props.agentId) ?? ''
}

watch(
  () => [props.petSlug, props.instanceKey, props.agentId, revision.value] as const,
  () => { void sync() },
  { immediate: true },
)

const url = computed(() => urlForSlug(resolvedSlug.value))
const plan = computed(() => planAvatar(props.avatarKind, resolvedSlug.value))
const glyphSize = computed(() => Math.max(10, Math.round(props.size * 0.58)))
</script>

<template>
  <span
    class="agent-pet-avatar"
    :style="{ '--agent-avatar-size': `${size}px` }"
    :data-pet-slug="resolvedSlug || undefined"
    aria-hidden="true"
  >
    <img v-if="plan.kind === 'sprite' && url" :src="url" :alt="alt" draggable="false" />
    <!-- Imported, but this build has no Live2D renderer. A distinct glyph (never a sprite) keeps
         the row honest at 16-44px where the full sentence would not fit. -->
    <Layers
      v-else-if="plan.kind === 'live2d'"
      :size="glyphSize"
      class="agent-pet-avatar-live2d"
      :title="t('settings.companions.live2d.unavailableShort')"
    />
    <PawPrint v-else :size="glyphSize" class="agent-pet-avatar-fallback" />
  </span>
</template>

<style scoped>
.agent-pet-avatar {
  position: relative;
  display: inline-block;
  flex: none;
  width: var(--agent-avatar-size);
  aspect-ratio: 192 / 208;
  overflow: hidden;
  vertical-align: middle;
}

/*
 * The sheet is 8 frames wide; at 800% the image is exactly eight boxes wide, so the crop is
 * whatever sits at the box's origin. It must be pinned there — a flex/grid centring context
 * would centre the oversized image instead and show a frame from the middle of the atlas.
 */
.agent-pet-avatar img {
  position: absolute;
  top: 0;
  left: 0;
  display: block;
  width: 800%;
  max-width: none;
  height: auto;
  image-rendering: pixelated;
  user-select: none;
  pointer-events: none;
}

.agent-pet-avatar-fallback {
  position: absolute;
  inset: 0;
  margin: auto;
  color: var(--text-muted);
}

.agent-pet-avatar-live2d {
  position: absolute;
  inset: 0;
  margin: auto;
  color: var(--accent-primary, var(--text-secondary));
}
</style>
