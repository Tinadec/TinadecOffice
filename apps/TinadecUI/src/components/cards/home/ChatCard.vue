<script setup lang="ts">
import ChatPanel from '@/components/ChatPanel.vue'
import BuddyChatPanel from '@/components/companion/BuddyChatPanel.vue'
import { computed, onMounted, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { Bot, Settings, X } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import { homeController } from '@/controllers/HomeController'
import { buddyCompanion, failures as companionFailures, loading as companionsLoading, refreshCompanionCenter } from '@/lib/companionStore'
import { buddyViewActive, closeBuddyView } from '@/lib/buddyView'
import { useHostAccess } from '@/lib/hostAccess'
import { requestSettingsSection } from '@/lib/pageRequests'

const c = homeController
const { t } = useI18n()
const router = useRouter()
const { canAccessBackend } = useHostAccess()

// The buddy is a second occupant of the same card, not a second card: the chat card is singleton
// and not closable, so the buddy view swaps its content and the sidebar entry toggles it back.
// The sidebar already reads the companion domain once; the card only asks again when the buddy
// view is actually opened, so an ordinary chat card costs no companion reads.
const refreshBuddy = () => { if (buddyViewActive.value && canAccessBackend.value) void refreshCompanionCenter() }
onMounted(refreshBuddy)
watch(buddyViewActive, (active) => { if (active) refreshBuddy() })
watch(canAccessBackend, (ready) => { if (ready) refreshBuddy() })

function openBuddySettings() {
  requestSettingsSection('companions')
  void router.push('/settings')
}

// "No buddy designated" is a fact only when the reads that decide it succeeded; a failed read
// must not be reported as "you never chose one".
const buddyReadFailed = computed(() => Boolean(companionFailures.value.companions || companionFailures.value.settings))

// `busy` means "the send is still on its way", not "a run is working". A run's progress is the
// transcript's job (live work rows, answer drafting); folding `working` in here made the send
// button spin and announce "sending" for the entire run, so the moment Core accepted the
// message was indistinguishable from the moment the answer arrived. Stopping a run reads
// `canStop`, which spans the run as it should.
//
// Supervision escalations reach the flat feed through its own reducer (they are run state
// there), so this card no longer rebuilds them from the activity store. That map only ever
// held runs the activity composable had last selected, which is why an escalation on any
// other run had no visible gate. Space mode still reads the composable directly.
const conversation = computed(() => ({
  feed: c.conversationFeed,
  runs: c.runs.value,
  streamingReplies: c.streamingReplies.value,
  approvals: c.approvals.value,
  workspaceRoot: c.currentProject.value?.path ?? null,
}))
</script>

<template>
  <div v-if="buddyViewActive" class="buddy-view">
    <UiButton
      variant="ghost"
      size="icon"
      class="buddy-view-close"
      :aria-label="t('common.close')"
      :title="t('common.close')"
      @click="closeBuddyView()"
    >
      <X :size="16" />
    </UiButton>
    <BuddyChatPanel v-if="buddyCompanion" :companion="buddyCompanion" />
    <div v-else-if="!canAccessBackend" class="buddy-view-empty" role="status">
      <p>{{ t('host.previewReason') }}</p>
    </div>
    <div v-else-if="companionsLoading" class="buddy-view-empty" role="status">{{ t('buddy.loading') }}</div>
    <div v-else-if="buddyReadFailed" class="buddy-view-empty" role="alert">
      <p>{{ t('buddy.loadFailed') }}</p>
      <UiButton variant="outline" size="sm" @click="refreshBuddy">{{ t('buddy.retry') }}</UiButton>
    </div>
    <div v-else class="buddy-view-empty">
      <Bot :size="28" aria-hidden="true" />
      <h2>{{ t('buddy.empty') }}</h2>
      <p>{{ t('buddy.emptyHint') }}</p>
      <UiButton size="sm" @click="openBuddySettings">
        <Settings :size="14" />
        <span>{{ t('buddy.openSettings') }}</span>
      </UiButton>
    </div>
  </div>
  <ChatPanel
    v-else
    :messages="c.messages.value"
    :sessions="c.sessions.value"
    :projects="c.projects.value"
    :current-session="c.currentSession.value"
    :current-project="c.currentProject.value"
    :selected-project-id="c.selectedProjectId.value"
    :model-name="c.modelName.value"
    :orchestration="c.orchestration.value"
    :busy="c.busy.value"
    :draft="c.draft.value"
    :permission="c.currentPermission.value"
    :composer-settings="c.composerSettings.value"
    :settings-saving="c.settingsSaving.value"
    :settings-error="c.settingsError.value"
    :runs-for-composer="c.runs.value"
    :can-stop="Boolean(c.stoppableRunId.value)"
    :conversation="conversation"
    :loading="c.transcriptLoading.value"
    @update:draft="c.updateDraft($event)"
    @update:permission="c.updatePermission($event)"
    @update:settings="c.updateComposerSettings($event)"
    @send="c.sendMessage($event)"
    @welcome-send="c.handleWelcomeSend($event)"
    @create-project="c.openProject()"
    @select-project="c.setSelectedProject($event)"
    @edit-message="c.editAndResend($event)"
    @stop="c.stopRun()"
  />
</template>

<style scoped>
.buddy-view {
  position: relative;
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  padding: 18px;
}

.buddy-view-close {
  position: absolute;
  top: 10px;
  right: 10px;
  z-index: 1;
}

/* The panel's own header starts at the left edge; keep the close button off its name/tagline. */
.buddy-view :deep(.buddy-chat-head) {
  padding-right: 34px;
}

.buddy-view-empty {
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

.buddy-view-empty h2 {
  margin: 0;
  font-size: 16px;
  color: var(--text-primary);
}

.buddy-view-empty p {
  margin: 0;
  max-width: 420px;
  font-size: 13px;
  line-height: 1.6;
}
</style>
