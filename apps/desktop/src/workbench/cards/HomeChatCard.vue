<script setup lang="ts">
import ChatPanel from '@/components/ChatPanel.vue'
import { useHomeWorkbench } from '../homeController'

const home = useHomeWorkbench()
</script>

<template>
  <ChatPanel
    :messages="home.messages.value"
    :sessions="home.sessions.value"
    :projects="home.projects.value"
    :current-session="home.currentSession.value"
    :current-project="home.currentProject.value"
    :selected-project-id="home.selectedProjectId.value"
    :model-name="home.modelName.value"
    :orchestration="home.orchestration.value"
    :busy="home.busy.value"
    :draft="home.draft.value"
    :mode="home.currentMode.value"
    :permission="home.currentPermission.value"
    :thinking-steps="home.agentThinkingSteps.value"
    :tool-calls="home.agentToolCalls.value"
    :agent-label="home.agentLabel.value"
    @update:draft="home.draft.value = $event"
    @update:mode="home.currentMode.value = $event"
    @update:permission="home.currentPermission.value = $event"
    @send="home.sendMessage"
    @welcome-send="home.handleSend"
    @create-project="home.openProject"
    @select-project="home.selectedProjectId.value = $event"
  />
</template>

<style scoped>
:deep(.conversation) {
  position: relative;
  inset: auto;
  width: 100%;
  height: 100%;
  overflow: hidden;
}
</style>
