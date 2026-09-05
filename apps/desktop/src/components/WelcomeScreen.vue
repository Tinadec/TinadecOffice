<script setup lang="ts">
import { ref, computed } from 'vue'
import { MessageCircle, SquareTerminal } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { UiButton } from '@/components/ui'

const { t } = useI18n()

const isChatMode = ref(false)

const titleText = computed(() =>
  isChatMode.value ? t('chat.chatWithTinadec') : t('chat.startProject')
)

function toggleChatMode() {
  isChatMode.value = !isChatMode.value
}
</script>

<template>
  <!-- Hero title block only: the composer itself is the persistent ComposerBar
       mounted by ChatPanel outside this branch, so it never remounts here. -->
  <div class="welcome-screen">
    <div class="welcome-content">
      <div class="welcome-title-row">
        <Transition name="title-fade" mode="out-in">
          <h1 :key="titleText" class="welcome-title">{{ titleText }}</h1>
        </Transition>
        <UiButton
          variant="ghost"
          size="icon"
          class="welcome-title-action"
          :title="isChatMode ? t('chat.terminal') : t('chat.chatMode')"
          @click="toggleChatMode"
        >
          <MessageCircle v-if="isChatMode" :size="15" />
          <SquareTerminal v-else :size="15" />
        </UiButton>
      </div>
    </div>
  </div>
</template>
