<script setup lang="ts">
import ChatPanel from '@/components/ChatPanel.vue'
import { computed } from 'vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController

// Supervision escalations are still read from the activity composable that space mode
// keeps using; the flat feed renders them through the shared SupervisionGate.
const conversation = computed(() => ({
  feed: c.conversationFeed,
  runs: c.runs.value,
  streamingReplies: c.streamingReplies.value,
  approvals: c.approvals.value,
  supervisionReviews: Object.fromEntries(Object.entries(c.agentTurnActivities.value).map(([runId, turn]) => [runId, turn.supervisionReview ?? null])),
  workspaceRoot: c.currentProject.value?.path ?? null,
}))
</script>

<template>
  <ChatPanel
    :messages="c.messages.value"
    :sessions="c.sessions.value"
    :projects="c.projects.value"
    :current-session="c.currentSession.value"
    :current-project="c.currentProject.value"
    :selected-project-id="c.selectedProjectId.value"
    :model-name="c.modelName.value"
    :orchestration="c.orchestration.value"
    :busy="c.busy.value || c.working.value"
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
