<script setup lang="ts">
import ChatPanel from '@/components/ChatPanel.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController
</script>

<template vapor>
  <ChatPanel
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
    :mode="c.currentMode.value"
    :permission="c.currentPermission.value"
    :thinking-steps="c.agentThinkingSteps.value"
    :tool-calls="c.agentToolCalls.value"
    :agent-label="c.agentLabel.value"
    @update:draft="c.updateDraft($event)"
    @update:mode="c.updateMode($event)"
    @update:permission="c.updatePermission($event)"
    @send="c.sendMessage()"
    @welcome-send="c.handleWelcomeSend($event)"
    @create-project="c.openProject()"
    @select-project="c.setSelectedProject($event)"
  />
</template>
