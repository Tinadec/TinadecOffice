<script setup lang="ts">
import { ref } from 'vue'
import type { AgentDefinitionDto, AgentModeEdgeDto, AgentModeNodeDto } from '@/api'
import ModeGraphCanvas from '@/components/modeEditor/ModeGraphCanvas.vue'
import type { ModeLane } from '@/components/modeEditor/modeDagreLayout'

defineProps<{
  nodes: AgentModeNodeDto[]
  edges: AgentModeEdgeDto[]
  agents: AgentDefinitionDto[]
  readonly?: boolean
  selectedNodeId?: string | null
  selectedEdgeId?: string | null
  graphKey?: string | null
}>()

const emit = defineEmits<{
  'select-node': [id: string | null]
  'select-edge': [id: string | null]
  'node-moved': [payload: { id: string; position: { x: number; y: number }; lane?: ModeLane }]
  connect: [payload: { source: string; target: string }]
  'update-edge': [payload: { id: string; source: string; target: string }]
  'delete-node': [id: string]
  'delete-edge': [id: string]
  'clone-node': [id: string]
  'set-conversation': [id: string]
  'add-node': [payload: { lane: ModeLane }]
  'auto-layout': []
  undo: []
  redo: []
}>()

const innerRef = ref<InstanceType<typeof ModeGraphCanvas> | null>(null)

defineExpose({
  fitView: () => innerRef.value?.fitView(),
  zoomIn: () => innerRef.value?.zoomIn(),
  zoomOut: () => innerRef.value?.zoomOut(),
  connect: (connection: Parameters<NonNullable<InstanceType<typeof ModeGraphCanvas>['connect']>>[0]) =>
    innerRef.value?.connect(connection),
})
</script>

<template>
  <ModeGraphCanvas
    ref="innerRef"
    :nodes="nodes"
    :edges="edges"
    :agents="agents"
    :readonly="readonly"
    :selected-node-id="selectedNodeId"
    :selected-edge-id="selectedEdgeId"
    :graph-key="graphKey"
    @select-node="emit('select-node', $event)"
    @select-edge="emit('select-edge', $event)"
    @node-moved="emit('node-moved', $event)"
    @connect="emit('connect', $event)"
    @update-edge="emit('update-edge', $event)"
    @delete-node="emit('delete-node', $event)"
    @delete-edge="emit('delete-edge', $event)"
    @clone-node="emit('clone-node', $event)"
    @set-conversation="emit('set-conversation', $event)"
    @add-node="emit('add-node', $event)"
    @auto-layout="emit('auto-layout')"
    @undo="emit('undo')"
    @redo="emit('redo')"
  />
</template>
