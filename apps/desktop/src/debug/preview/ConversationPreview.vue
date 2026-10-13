<script setup lang="ts">
/**
 * 对话消息流预览：用真实的 ConversationView 渲染共享 fixtures（与数据层测试同一批事件序列）。
 * 每个场景都是一整段会话——消息、run 行、Core 会写出的会话 SSE 事件——经真实 reducer 归约，
 * 所以这里看到的就是平面模式在同样事件下的样子：简单问答、探索+编辑+shell、待审批、
 * 并行子智能体、失败、打断、上下文压缩。
 */
import { computed, ref, shallowRef } from 'vue'
import ConversationView from '@/components/conversation/ConversationView.vue'
import type { ConversationFeed } from '@/composables/useConversationFeed'
import { FEED_FIXTURES } from '@/lib/conversation/fixtures'
import { applyEvents, createFeedState } from '@/lib/conversation/reducer'

const scenarios = FEED_FIXTURES.map((make) => make())
const selected = ref(scenarios[0]!.name)
/** Stands in for a session switch whose transcript has not arrived: the skeleton after 200ms. */
const loading = ref(false)
const scenario = computed(() => scenarios.find((item) => item.name === selected.value) ?? scenarios[0]!)

const feed = computed<ConversationFeed>(() => {
  const state = createFeedState()
  applyEvents(state, scenario.value.events)
  return { state: shallowRef(state), revision: ref(1), hydrated: ref(true) }
})
</script>

<template>
  <div class="conversation-preview">
    <div class="conversation-preview-scenarios" role="radiogroup" aria-label="场景">
      <button
        v-for="item in scenarios"
        :key="item.name"
        type="button"
        role="radio"
        class="conversation-preview-scenario"
        :aria-checked="item.name === selected"
        @click="selected = item.name"
      >
        {{ item.name }}
      </button>
      <button type="button" class="conversation-preview-scenario" role="switch" :aria-checked="loading" @click="loading = !loading">加载中</button>
    </div>
    <div class="conversation-preview-inner">
      <ConversationView
        :key="scenario.name"
        :messages="loading ? [] : scenario.messages"
        :runs="loading ? [] : scenario.runs"
        :loading="loading"
        :feed="feed"
        workspace-root="/w"
      />
    </div>
    <div class="conversation-preview-hint">fixtures 与 src/lib/conversation 测试共用 · 运行中场景的工作区展开，已完成场景收成摘要，点击可展开</div>
  </div>
</template>

<style scoped>
.conversation-preview {
  height: 100%;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.conversation-preview-scenarios {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 10px 16px 0;
}

.conversation-preview-scenario {
  padding: 2px 10px;
  border: 1px solid var(--border-card);
  border-radius: 999px;
  color: var(--text-secondary);
  background: transparent;
  font-size: 11px;
  cursor: pointer;
}

.conversation-preview-scenario[aria-checked='true'] {
  color: var(--text-primary);
  background: var(--surface-selected);
}

.conversation-preview-inner {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.conversation-preview-hint {
  flex-shrink: 0;
  padding: 8px 16px 12px;
  text-align: center;
  font-size: 11px;
  color: var(--text-muted, #8b949e);
}
</style>
