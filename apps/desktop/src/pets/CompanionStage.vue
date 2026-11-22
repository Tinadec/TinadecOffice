<script setup lang="ts">
import CompanionStage, { type StagePetState } from '@/components/pets/CompanionStage.vue'
import type { AvatarKind } from './avatarKind'

defineProps<{
  petSlug: string
  avatarKind?: AvatarKind
  state?: StagePetState
  speaking?: boolean
  mouthOpen?: number
  emotion?: string | null
  motionGroup?: string | null
  entryModelJson?: string | null
  height?: number
  alt?: string
}>()
</script>

<template>
  <CompanionStage v-bind="$props" />
</template>
