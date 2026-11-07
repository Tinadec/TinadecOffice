<script setup lang="ts">
/**
 * The buddy conversation surface. The sidebar entry below "new chat" opens this page; it shows the
 * one designated buddy's private chat. There is exactly one buddy conversation — it is ensured,
 * never newly created. When no buddy is designated yet, the page points at settings instead of
 * inventing a companion.
 */
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { Bot, Settings } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import BuddyChatPanel from '@/components/companion/BuddyChatPanel.vue'
import { useCompanionCenter } from '@/lib/companionStore'

const { t } = useI18n()
const router = useRouter()
const { buddyCompanion, loading } = useCompanionCenter()

const hasBuddy = computed(() => buddyCompanion.value !== null)

function openSettings() {
  void router.push({ name: 'settings' })
}
</script>

<template>
  <div class="buddy-page">
    <div class="buddy-page-card">
      <template v-if="hasBuddy && buddyCompanion">
        <BuddyChatPanel :companion="buddyCompanion" />
      </template>
      <div v-else-if="loading" class="buddy-page-empty" role="status">{{ t('buddy.loading') }}</div>
      <div v-else class="buddy-page-empty">
        <Bot :size="28" aria-hidden="true" />
        <h2>{{ t('buddy.empty') }}</h2>
        <p>{{ t('buddy.emptyHint') }}</p>
        <UiButton size="sm" @click="openSettings">
          <Settings :size="14" />
          <span>{{ t('buddy.openSettings') }}</span>
        </UiButton>
      </div>
    </div>
  </div>
</template>

<style scoped>
.buddy-page {
  display: flex;
  justify-content: center;
  padding: 24px 16px;
  height: 100%;
  min-height: 0;
}

.buddy-page-card {
  display: flex;
  flex-direction: column;
  width: min(720px, 100%);
  height: 100%;
  min-height: 0;
  padding: 18px;
  border-radius: 14px;
  background: var(--surface-section, var(--surface-raised));
}

.buddy-page-empty {
  display: flex;
  flex: 1;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  text-align: center;
  color: var(--text-muted);
}

.buddy-page-empty h2 {
  margin: 0;
  font-size: 16px;
  color: var(--text-primary);
}

.buddy-page-empty p {
  margin: 0;
  max-width: 420px;
  font-size: 13px;
  line-height: 1.6;
}
</style>