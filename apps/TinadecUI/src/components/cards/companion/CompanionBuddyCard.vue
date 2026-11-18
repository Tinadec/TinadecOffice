<script setup lang="ts">
/**
 * The centre of the companion page: one buddy conversation, with everything waiting on the user
 * pinned above it.
 *
 * There is exactly one buddy and exactly one conversation with it, so this card never offers
 * "new chat" — it either shows that conversation or points at Settings to designate a buddy.
 */
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Bot, Settings } from '@lucide/vue'
import BuddyChatPanel from '@/components/companion/BuddyChatPanel.vue'
import { useHostAccess } from '@/lib/hostAccess'
import { buddyCompanion, loading, useCompanionCenter } from '@/lib/companionStore'
import { requestSettingsSection } from '@/lib/pageRequests'
import CompanionAttentionStrip from './CompanionAttentionStrip.vue'

const { t } = useI18n()
const { canAccessBackend, reason } = useHostAccess()
useCompanionCenter()

const buddy = computed(() => buddyCompanion.value)

function openSettings(): void {
  requestSettingsSection('companions')
}
</script>

<template>
  <section class="companion-card companion-buddy-card">
    <CompanionAttentionStrip />

    <template v-if="buddy">
      <BuddyChatPanel :companion="buddy" />
    </template>
    <div v-else-if="!canAccessBackend" class="companion-buddy-empty" role="status">
      <Bot :size="26" aria-hidden="true" />
      <p>{{ reason }}</p>
    </div>
    <div v-else-if="loading" class="companion-buddy-empty" role="status">{{ t('companion.center.loading') }}</div>
    <div v-else class="companion-buddy-empty">
      <Bot :size="26" aria-hidden="true" />
      <h2>{{ t('buddy.empty') }}</h2>
      <p>{{ t('buddy.emptyHint') }}</p>
      <button type="button" class="companion-buddy-settings" @click="openSettings">
        <Settings :size="14" />
        <span>{{ t('buddy.openSettings') }}</span>
      </button>
    </div>
  </section>
</template>

<style scoped>
.companion-buddy-card {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  overflow: hidden;
}

.companion-buddy-empty {
  display: flex;
  flex: 1;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  padding: 24px;
  text-align: center;
  color: var(--text-muted);
}

.companion-buddy-empty h2 {
  margin: 0;
  font-size: 15px;
  color: var(--text-primary);
}

.companion-buddy-empty p {
  max-width: 420px;
  margin: 0;
  font-size: 12.5px;
  line-height: 1.6;
}

.companion-buddy-settings {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-top: 4px;
  padding: 5px 12px;
  border: 1px solid var(--border-muted);
  border-radius: 7px;
  font-size: 12px;
  color: var(--text-primary);
  background: var(--surface-raised);
  cursor: pointer;
}
</style>
