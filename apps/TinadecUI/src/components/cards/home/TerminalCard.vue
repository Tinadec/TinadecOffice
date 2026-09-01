<script setup lang="ts">
import { computed, inject, toValue, type MaybeRefOrGetter } from 'vue'
import TerminalPanel from '@/components/TerminalPanel.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController

// Card context is provided by UieCardHost; `toValue` also accepts a host that
// still provides a plain boolean.
const active = inject<MaybeRefOrGetter<boolean>>('wb:active', false)

const visible = computed(() => toValue(active) ?? false)
</script>

<template vapor>
  <TerminalPanel
    :cwd="c.currentProject.value?.path"
    :visible="visible"
  />
</template>
