<script setup lang="ts">
/**
 * The `/buddy` deep link. The buddy is not a page of its own any more: it is a view of the one
 * chat card (and the space drawer). Entering this route turns that view on and hands the window
 * back to the work pages, so a bookmark or an old link lands on the real surface instead of a
 * second copy of the conversation.
 *
 * The markup below is only what the redirect shows in the frame before it happens (and what the
 * deep link renders when navigation is unavailable); it deliberately reuses the same panel and
 * empty state rather than laying out another card.
 */
import { computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { Bot, Settings } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import BuddyChatPanel from '@/components/companion/BuddyChatPanel.vue'
import { useCompanionCenter } from '@/lib/companionStore'
import { openBuddyView } from '@/lib/buddyView'
import { requestSettingsSection } from '@/lib/pageRequests'

const { t } = useI18n()
const router = useRouter()
const { buddyCompanion, loading, failures, refresh } = useCompanionCenter()

const hasBuddy = computed(() => buddyCompanion.value !== null)
// A failed read is not "no buddy designated": show the failure and a retry instead of pointing
// the user at a setting that may already be correct.
const readFailed = computed(() => Boolean(failures.value.companions || failures.value.settings))

onMounted(() => {
  openBuddyView()
  void router.push('/')
})

function openSettings() {
  requestSettingsSection('companions')
  void router.push({ name: 'settings' })
}
</script>

<template>
  <div class="buddy-redirect">
    <BuddyChatPanel v-if="hasBuddy && buddyCompanion" :companion="buddyCompanion" />
    <div v-else-if="loading" class="buddy-redirect-empty" role="status">{{ t('buddy.loading') }}</div>
    <div v-else-if="readFailed" class="buddy-redirect-empty" role="alert">
      <p>{{ t('buddy.loadFailed') }}</p>
      <UiButton variant="outline" size="sm" @click="refresh">{{ t('buddy.retry') }}</UiButton>
    </div>
    <div v-else class="buddy-redirect-empty">
      <Bot :size="28" aria-hidden="true" />
      <h2>{{ t('buddy.empty') }}</h2>
      <p>{{ t('buddy.emptyHint') }}</p>
      <UiButton size="sm" @click="openSettings">
        <Settings :size="14" />
        <span>{{ t('buddy.openSettings') }}</span>
      </UiButton>
    </div>
  </div>
</template>

<style scoped>
.buddy-redirect {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  padding: 24px 16px;
}

.buddy-redirect-empty {
  display: flex;
  flex: 1;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  text-align: center;
  color: var(--text-muted);
}

.buddy-redirect-empty h2 {
  margin: 0;
  font-size: 16px;
  color: var(--text-primary);
}

.buddy-redirect-empty p {
  margin: 0;
  max-width: 420px;
  font-size: 13px;
  line-height: 1.6;
}
</style>