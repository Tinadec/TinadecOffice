<script setup lang="ts">
/**
 * The local profile portrait, shared by Settings → Personal and the sidebar
 * footer.
 *
 * Read-only by construction: it renders whatever `lib/userProfile` holds —
 * avatar image, then the nickname's first letter, then the placeholder glyph —
 * and never writes to the store. The round frame is the one the product has
 * always shown (the settings hero used `UiAvatar`, which is `rounded-full`),
 * so both surfaces share it instead of the sidebar inventing a second shape.
 */
import { computed } from 'vue'
import { UserRound } from '@lucide/vue'
import { userProfile } from '@/lib/userProfile'

const props = withDefaults(defineProps<{
  size?: number
  /** Empty by default: the portrait is decorative inside an already-labelled control. */
  alt?: string
  /** Signed out there is no avatar to show: keep the frame, drop its contents. */
  empty?: boolean
}>(), {
  size: 28,
  alt: '',
  empty: false,
})

const initial = computed(() => userProfile.nickname.trim().slice(0, 1).toUpperCase())

const frameStyle = computed(() => ({
  width: `${props.size}px`,
  height: `${props.size}px`,
  fontSize: `${Math.round(props.size * 0.375)}px`,
}))
</script>

<template>
  <span class="personal-avatar rounded-full" :style="frameStyle">
    <template v-if="!empty">
      <img v-if="userProfile.avatar" :src="userProfile.avatar" :alt="alt" />
      <span v-else-if="initial" class="personal-avatar-initial">{{ initial }}</span>
      <UserRound v-else :size="Math.round(size * 0.47)" class="personal-avatar-glyph" />
    </template>
  </span>
</template>

<style scoped>
.personal-avatar {
  display: grid;
  place-items: center;
  flex: 0 0 auto;
  overflow: hidden;
  border-radius: 9999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

.personal-avatar img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.personal-avatar-initial {
  font-weight: 600;
  line-height: 1;
  color: var(--text-secondary);
}

.personal-avatar-glyph {
  color: var(--text-muted);
}
</style>
